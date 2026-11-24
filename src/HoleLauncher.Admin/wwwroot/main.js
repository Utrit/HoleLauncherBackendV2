const startup = document.getElementById("startup");
try {
    const { dotnet } = await import("./_framework/dotnet.js");
    const runtime = await dotnet.create();
    runtime.setModuleImports("admin", { getOrigin: () => window.location.origin });
    await runtime.runMain(runtime.getConfig().mainAssemblyName, []);
    startup.remove();
} catch (error) {
    startup.removeAttribute("aria-label");
    startup.textContent = "Не удалось запустить HoleLauncher Admin.";
    console.error(error);
}
