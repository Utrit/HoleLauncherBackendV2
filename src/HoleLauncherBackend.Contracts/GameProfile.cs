namespace HoleLauncherBackend.Contracts;

public sealed record GameProfile(string? GameName, Guid? SkinFileId, Guid? CapeFileId, string[]? RecentNames = null, string SkinModel = SkinModels.Classic);
public sealed record GameNameChange(string? GameName);
public sealed record GameTextureChange(Guid FileId, string? SkinModel = null);
public sealed record GameTextureCommand(Guid FileId, bool Skin, string Model);

public static class SkinModels
{
    public const string Classic = "classic";
    public const string Slim = "slim";

    public static bool IsValid(string model) => model is Classic or Slim;
}
public sealed record DraslSession(string AccessToken, string ClientToken, DraslPlayer SelectedProfile);
public sealed record DraslPlayer(string Id, string Name);
