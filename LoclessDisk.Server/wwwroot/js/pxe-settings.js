import { requestJson } from "./lib/http.js";

const form = document.getElementById("pxeForm");
const address = document.getElementById("pxeAddress");
const bootScript = document.getElementById("pxeBootScript");
const enabled = document.getElementById("pxeEnabled");
const note = document.getElementById("pxeNote");
const save = form.querySelector("button");

save.disabled = true;
try {
  const settings = await requestJson("/api/settings/pxe");
  for (const item of settings.interfaces)
    address.add(new Option(item.name + " · " + item.address, item.address));
  if (settings.bindAddress && !settings.interfaces.some(item => item.address === settings.bindAddress))
    address.add(new Option(settings.bindAddress + " · сейчас недоступен", settings.bindAddress));
  address.value = settings.bindAddress ?? "";
  enabled.checked = settings.enabled;
  bootScript.value = settings.bootScriptUrl ?? "";
  save.disabled = false;
} catch (error) {
  note.textContent = error.message;
}

form.addEventListener("submit", async event => {
  event.preventDefault();
  save.disabled = true;
  note.classList.remove("saved");
  try {
    const settings = await requestJson("/api/settings/pxe", {
      method: "PUT", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ bindAddress: address.value || null, enabled: enabled.checked, bootScriptUrl: bootScript.value })
    });
    note.textContent = settings.enabled
      ? "Сохранено. Сервер применит настройку в течение нескольких секунд."
      : "Сохранено. Службы PXE выключены.";
    note.classList.add("saved");
  } catch (error) {
    note.textContent = error.message;
  } finally {
    save.disabled = false;
  }
});
