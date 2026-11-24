using System.Net;
using Confluent.Kafka;
using HoleLauncherBackend.Contracts;
using HoleLauncherBackend.Messaging;
using Microsoft.AspNetCore.Http.Features;
using MinDataRate = Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace HoleLauncherBackend.Api.Endpoints;

internal sealed class StorageProxy(AccountManagementProxy accounts, IHttpClientFactory clients, KafkaBus bus,
    IConfiguration configuration, ILogger<StorageProxy> logger)
{
    public async Task<IResult> UploadAsync(string domain, HttpContext context)
    {
        var request = context.Request;
        if (!StorageAddress.IsPublicDomain(domain))
            return Results.BadRequest();
        if (request.ContentLength > StorageAddress.MaxUploadBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false })
            bodyLimit.MaxRequestBodySize = StorageAddress.MaxUploadBytes;

        var actor = await accounts.SendAsync(context, AccessOperation.StartStorageUpload, context.RequestAborted,
            storageDomain: domain);
        if (actor.Outcome != AccessOutcome.Accepted || actor.Operation is not { } operation || actor.ActorId is not { } uploader)
            return AccountManagementProxy.Result(actor);

        if (request.HttpContext.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } dataRate)
            dataRate.MinDataRate = new MinDataRate(
                Math.Max(1, configuration.GetValue("Storage:MinUploadBytesPerSecond", 500_000)),
                TimeSpan.FromSeconds(30));

        var id = operation.OperationId;
        var storageDomain = StorageAddress.ResolveDomain(domain, uploader);
        var complete = false;
        var status = HttpStatusCode.ServiceUnavailable;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromHours(1));
        try
        {
            using var upload = new HttpRequestMessage(HttpMethod.Put, $"staging/{storageDomain}/{id:D}")
            {
                Content = new StreamContent(request.Body)
            };
            upload.Content.Headers.ContentLength = request.ContentLength;
            upload.Headers.Add("X-Uploader-Id", uploader.ToString("D"));
            using var response = await clients.CreateClient("storage")
                .SendAsync(upload, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            status = response.StatusCode;
            complete = status == HttpStatusCode.NoContent && !deadline.IsCancellationRequested;
        }
        catch (BadHttpRequestException error)
        {
            status = (HttpStatusCode)error.StatusCode;
        }
        catch (HttpRequestException error) when (error.InnerException is BadHttpRequestException rejected)
        {
            status = (HttpStatusCode)rejected.StatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            logger.LogDebug("Storage transfer did not complete: {ErrorType}", error.GetType().Name);
        }

        // A disconnected client must not cancel delivery of the abort command.
        using var reportDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            await bus.PublishAsync(KafkaBus.StorageTransfersTopic, id.ToString("N"),
                new StorageTransfer(id, complete, status == HttpStatusCode.RequestTimeout), reportDeadline.Token);
        }
        catch (Exception error) when (error is KafkaException or OperationCanceledException)
        {
            logger.LogWarning("Could not report storage transfer {OperationId}", id);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!complete)
            return Results.StatusCode(status switch
            {
                HttpStatusCode.RequestEntityTooLarge => StatusCodes.Status413PayloadTooLarge,
                HttpStatusCode.RequestTimeout => StatusCodes.Status408RequestTimeout,
                _ => StatusCodes.Status503ServiceUnavailable
            });

        return Results.Accepted($"/api/auth/operations/{id:D}", new StorageUploadAccepted(id, domain, id));
    }

    public async Task ForwardAsync(string domain, string fileId, HttpMethod method, string permission, HttpContext context,
        bool metadata = false)
    {
        if (!StorageAddress.IsPublicDomain(domain) || !StorageAddress.TryParseFileId(fileId, out var id))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var cancellation = context.RequestAborted;
        var reply = await accounts.SendAsync(context, AccessOperation.Session, cancellation);
        if (reply is not { Outcome: AccessOutcome.Ok, Account: { } account })
        {
            await AccountManagementProxy.Result(reply).ExecuteAsync(context);
            return;
        }
        if (!account.Permissions.Contains(permission))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        var storageDomain = StorageAddress.ResolveDomain(domain, account.AccountId);
        using var request = new HttpRequestMessage(method, $"files/{storageDomain}/{id:D}" + (metadata ? "/metadata" : ""));
        if (!metadata && method == HttpMethod.Get && context.Request.Headers.TryGetValue("Range", out var range))
            request.Headers.TryAddWithoutValidation("Range", range.ToArray());

        try
        {
            using var response = await clients.CreateClient("storage")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.StatusCode is HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                context.Response.Headers.ContentRange = response.Content.Headers.ContentRange?.ToString();
                return;
            }
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                return;

            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            context.Response.ContentLength = response.Content.Headers.ContentLength;
            context.Response.Headers.ContentDisposition = response.Content.Headers.ContentDisposition?.ToString();
            context.Response.Headers.ContentRange = response.Content.Headers.ContentRange?.ToString();
            if (!metadata)
                context.Response.Headers.AcceptRanges = "bytes";

            await response.Content.CopyToAsync(context.Response.Body, cancellation);
        }
        catch (HttpRequestException) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
    }
}
