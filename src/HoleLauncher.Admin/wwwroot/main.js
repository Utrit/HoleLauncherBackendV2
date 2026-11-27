const localeKey = "holelauncher.locale";
const savedLanguage = localStorage.getItem(localeKey);
const language = savedLanguage === "en" ? "en" : "ru";
document.documentElement.lang = language;
let dictionary = {};

async function loadLocale(code) {
    const response = await fetch(`/${code}.locale`);
    if (!response.ok) throw new Error(`Locale load failed: ${response.status}`);
    return response.json();
}

try {
    dictionary = await loadLocale(language);
} catch {
    try { dictionary = await loadLocale(language === "ru" ? "en" : "ru"); } catch { }
}

function translate(key) {
    return dictionary[key] ?? key;
}

function setLanguage(code) {
    if ((code !== "ru" && code !== "en") || code === language) return;
    localStorage.setItem(localeKey, code);
    window.location.reload();
}

for (const element of document.querySelectorAll("[data-locale]")) {
    element.textContent = translate(element.dataset.locale);
}
for (const element of document.querySelectorAll("[data-locale-aria]")) {
    element.setAttribute("aria-label", translate(element.dataset.localeAria));
}

const startup = document.getElementById("startup");
const loginView = document.getElementById("login-view");
const loginForm = document.getElementById("login-form");
const usernameInput = document.getElementById("login-username");
const passwordInput = document.getElementById("login-password");
const passwordToggle = document.getElementById("toggle-password");
const register = document.getElementById("register-button");
const loginMessage = document.getElementById("login-message");
const location = new URL(window.location.href);
const loginErrors = new Map([
    ["invalid", translate("auth.invalid")],
    ["limited", translate("auth.limited")],
    ["unavailable", translate("common.server_unavailable")]
]);
if (location.searchParams.has("login")) {
    loginMessage.textContent = loginErrors.get(location.searchParams.get("login")) ?? "";
    location.searchParams.delete("login");
    history.replaceState(null, "", location);
}

function showLogin(visible, message) {
    loginView.hidden = !visible;
    if (message) loginMessage.textContent = message;
    if (!visible) loginMessage.textContent = "";
}

function revealPassword(visible) {
    passwordInput.type = visible ? "text" : "password";
    passwordToggle.setAttribute("aria-pressed", String(visible));
    passwordToggle.setAttribute("aria-label", visible ? translate("auth.password_hide") : translate("auth.password_show"));
}

passwordToggle.addEventListener("click", () => revealPassword(passwordInput.type === "password"));
loginForm.addEventListener("submit", () => revealPassword(false));
register.addEventListener("click", async () => {
    if (!loginForm.reportValidity()) return;
    register.disabled = true;
    try {
        const response = await fetch("/api/auth/register", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ userName: usernameInput.value, password: passwordInput.value })
        });
        loginMessage.textContent = new Map([
            [201, translate("auth.registered")],
            [202, translate("auth.registration_pending")],
            [400, translate("auth.invalid")],
            [409, translate("auth.exists")],
            [429, translate("auth.limited")]
        ]).get(response.status) ?? translate("auth.registration_failed");
    } catch {
        loginMessage.textContent = translate("common.server_unavailable");
    } finally {
        register.disabled = false;
    }
});
function registerRepositoryFind(active, open) {
    document.addEventListener("keydown", event => {
        if (!(event.ctrlKey || event.metaKey) || event.altKey || event.shiftKey || event.key.toLowerCase() !== "f" || !active()) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        open();
    }, true);
}

function registerBrowserPaste(focused, paste) {
    document.addEventListener("keydown", event => {
        if (!event.altKey && (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "v" && focused()) {
            // Keep the browser paste action and prevent Avalonia from reading the clipboard separately.
            event.stopImmediatePropagation();
        }
    }, true);
    document.addEventListener("paste", event => {
        if (!focused()) return;
        const text = event.clipboardData?.getData("text/plain");
        if (!text) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        paste(text);
    }, true);
}

let droppedRepositoryFile;
document.addEventListener("drop", event => {
    droppedRepositoryFile = event.dataTransfer?.files?.[0] ?? null;
    const file = droppedRepositoryFile;
    setTimeout(() => {
        if (droppedRepositoryFile === file) droppedRepositoryFile = null;
    }, 0);
}, true);

function takeRepositoryDrop() {
    const file = droppedRepositoryFile;
    droppedRepositoryFile = null;
    return file ?? null;
}

async function readRepositoryFile(file) {
    return new Uint8Array(await file.arrayBuffer());
}

function copyRepositoryBytes(data) {
    return data;
}

function createRepositoryFile(name, data) {
    return new File([data], name, { type: "application/octet-stream" });
}

function chooseRepositoryFile(image, zip) {
    return new Promise(resolve => {
        const input = document.createElement("input");
        input.type = "file";
        if (image) input.accept = "image/png,image/jpeg,image/webp";
        else if (zip) input.accept = ".zip,application/zip";
        input.hidden = true;
        const finish = () => {
            input.remove();
            resolve(input.files?.[0] ?? null);
        };
        input.addEventListener("change", finish, { once: true });
        input.addEventListener("cancel", finish, { once: true });
        document.body.append(input);
        input.click();
    });
}

let sessionExpired;

function registerSessionExpiry(expired) {
    sessionExpired = expired;
}

function uploadRepositoryFile(path, file, progress) {
    return new Promise((resolve, reject) => {
        const xhr = new XMLHttpRequest();
        let first = performance.now();
        xhr.open("POST", path);
        xhr.timeout = 60 * 60 * 1000;
        xhr.setRequestHeader("Content-Type", "application/octet-stream");
        xhr.setRequestHeader("X-Storage-File-Name", encodeURIComponent(file.name));
        xhr.upload.addEventListener("progress", event => progress(
            event.loaded, file.size, event.loaded * 1000 / Math.max(1, performance.now() - first)));
        xhr.onload = () => {
            if (xhr.status === 401) sessionExpired?.();
            resolve(JSON.stringify({ status: xhr.status, body: xhr.responseText }));
        };
        xhr.onerror = xhr.ontimeout = xhr.onabort = () => reject(new Error("Repository upload interrupted"));
        first = performance.now();
        xhr.send(file);
    });
}

async function downloadRepositoryArchive(path, fallback) {
    const response = await fetch(path, { signal: AbortSignal.timeout(15 * 60 * 1000) });
    if (response.status === 401) sessionExpired?.();
    if (!response.ok) return response.status;
    const match = response.headers.get("Content-Disposition")?.match(/filename="?([^";]+)"?/i);
    const url = URL.createObjectURL(await response.blob());
    const link = document.createElement("a");
    link.href = url;
    link.download = match?.[1] ?? fallback;
    document.body.append(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 60_000);
    return response.status;
}


try {
    const { dotnet } = await import("./_framework/dotnet.js");
    const runtime = await dotnet.create();
    runtime.setModuleImports("admin", { getOrigin: () => window.location.origin, showLogin, translate, registerSessionExpiry, registerRepositoryFind, registerBrowserPaste, readRepositoryFile, copyRepositoryBytes, createRepositoryFile, chooseRepositoryFile, takeRepositoryDrop, uploadRepositoryFile, downloadRepositoryArchive, getLanguage: () => language, setLanguage });
    await runtime.runMain(runtime.getConfig().mainAssemblyName, []);
    startup.remove();
} catch (error) {
    startup.removeAttribute("aria-label");
    startup.textContent = translate("common.startup_failed");
    console.error(error);
}
