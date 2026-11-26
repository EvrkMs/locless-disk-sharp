import { requestJson } from "./lib/http.js";

const byId = id => document.getElementById(id);
const settingsForm = byId("settingsForm");
const imageDir = byId("imageDir");
const scanButton = byId("scan");
const errorBox = byId("error");

function showError(error) {
  errorBox.textContent = error?.message ?? "";
  errorBox.classList.toggle("show", Boolean(error));
}

function setConnected(connected) {
  byId("dot").classList.toggle("off", !connected);
  byId("link").textContent = connected ? "подключено" : "нет связи";
}

function route() {
  const hash = location.hash.slice(1);
  const page = ["images", "tasks", "settings"].includes(hash) ? hash : "pcs";
  for (const link of document.querySelectorAll("[data-page]")) {
    const selected = link.dataset.page === page;
    if (selected) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
    byId("page-" + link.dataset.page).hidden = !selected;
  }
}

function cell(value, className) {
  const td = document.createElement("td");
  td.textContent = value;
  if (className) td.className = className;
  return td;
}

async function showVersion(name, version) {
  showError();
  try {
    const result = await requestJson(version.href);
    byId("imageTitle").textContent = name + " · " + version.file;
    const details = byId("imageDetails");
    const summary = document.createElement("p");
    summary.textContent = "Размер блока: " + result.blockSize.toLocaleString("ru-RU") + " байт";
    const table = document.createElement("table");
    for (const partition of result.manifest.partitions) {
      const row = table.insertRow();
      row.append(cell(partition.number), cell(partition.name || partition.typeGuid),
        cell((partition.size / 2 ** 30).toLocaleString("ru-RU", { maximumFractionDigits: 2 }) + " ГиБ", "num"));
    }
    details.replaceChildren(summary, table);
    byId("imageDialog").showModal();
  } catch (error) {
    showError(error);
  }
}

async function loadImages() {
  const images = await requestJson("/api/images");
  const rows = document.createDocumentFragment();
  for (const image of images) {
    for (const [index, version] of [...image.versions].reverse().entries()) {
      const row = document.createElement("tr");
      const action = cell("", "actions");
      const button = document.createElement("button");
      button.textContent = "Подробнее";
      button.addEventListener("click", () => showVersion(image.name, version));
      action.append(button);
      if (index === 0) {
        const title = cell(image.name, "name");
        title.rowSpan = image.versions.length;
        title.title = image.path;
        row.append(title);
      }
      const kind = version.depth === 1 ? "полный" : "дельта";
      row.append(cell(version.file + " · " + kind, "mac"), cell(version.depth), action);
      rows.append(row);
    }
  }
  byId("images").replaceChildren(rows);
  byId("imageTable").hidden = images.length === 0;
  byId("noImages").hidden = images.length !== 0;
  byId("imageCount").textContent = images.length;
  document.dispatchEvent(new CustomEvent("images-updated", { detail: images }));
}

settingsForm.addEventListener("submit", async event => {
  event.preventDefault();
  showError();
  try {
    const settings = await requestJson("/api/settings", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ imageRoot: imageDir.value })
    });
    imageDir.value = settings.imageRoot;
    scanButton.disabled = false;
    byId("scanResult").hidden = true;
    byId("imageDirNote").textContent = "Папка сохранена. Сканирование доступно в разделе «Образы».";
    byId("imageDirNote").classList.add("saved");
    await loadImages();
  } catch (error) {
    showError(error);
  }
});

scanButton.addEventListener("click", async () => {
  scanButton.disabled = true;
  showError();
  const status = byId("scanResult");
  status.hidden = false;
  status.textContent = "Сканирование…";
  try {
    const result = await requestJson("/api/images/scan", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: "{}"
    });
    status.textContent = "Образов: " + result.images + ", версий: " + result.versions;
    for (const failure of result.failures) {
      const item = document.createElement("p");
      item.textContent = failure.file + ": " + failure.reason;
      status.append(item);
    }
    await loadImages();
  } catch (error) {
    status.hidden = true;
    showError(error);
  } finally {
    scanButton.disabled = false;
  }
});

window.addEventListener("hashchange", route);
route();
try {
  const settings = await requestJson("/api/settings");
  imageDir.value = settings.imageRoot ?? "";
  scanButton.disabled = !settings.imageRoot;
  setConnected(true);
  await loadImages();
} catch (error) {
  setConnected(false);
  showError(error);
}
