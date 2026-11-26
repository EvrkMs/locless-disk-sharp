import { button, openMenu } from "./lib/menu.js";
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

let catalog = [];
let computers = [];
let tasks = [];
let finishedClones;

async function loadImages() {
  catalog = await requestJson("/api/images");
  drawImages();
  document.dispatchEvent(new CustomEvent("images-updated", { detail: catalog }));
}

document.addEventListener("computers-updated", event => {
  computers = event.detail;
  drawImages();
});
document.addEventListener("tasks-updated", event => {
  tasks = event.detail;
  const latest = tasks.find(task => task.kind === "clone" && task.status === "succeeded")?.id;
  if (finishedClones !== latest) {
    finishedClones = latest;
    loadImages().catch(showError);
  }
  drawImages();
});

function drawImages() {
  const rows = document.createDocumentFragment();
  let unavailable = 0;
  for (const image of catalog) {
    const versions = [...image.versions].reverse();
    const current = image.versions.find(version => !version.disabled);
    const busy = tasks.some(task => task.imageId === image.id && task.finishedAt === null);
    const title = cell(image.name, "name");
    title.title = image.path;
    if (current?.missing) {
      unavailable++;
      const notice = document.createElement("div");
      notice.className = "sub lost";
      notice.textContent = "Файлы не найдены";
      title.append(notice);
    }
    const actions = cell("", "actions");
    const menu = button("Действия ▾", "", () => openMenu(menu, imageActions(image, current, busy)));
    menu.setAttribute("aria-haspopup", "menu");
    actions.append(menu);
    const common = [title, cell(computers.filter(pc => pc.imageId === image.id && pc.name !== null).length),
      cell(busy ? "Да" : "Нет")];
    if (!versions.length) {
      const row = document.createElement("tr");
      const empty = cell("версий пока нет, клонируйте эталон", "when");
      empty.colSpan = 3;
      row.append(...common, empty, actions);
      rows.append(row);
      continue;
    }
    for (const [index, version] of versions.entries()) {
      const row = document.createElement("tr");
      const versionCell = cell("", version.missing ? "mac lost" : "mac");
      const label = version.file + (version.missing ? " · файл не найден"
        : version.disabled ? " · отключена" : version === current ? " · текущая" : "");
      if (version.missing) versionCell.textContent = label;
      else versionCell.append(button(label, "quiet", () => showVersion(image.name, version)));
      const size = version.size == null ? "—" : (version.size / 2 ** 30)
        .toLocaleString("ru-RU", { maximumFractionDigits: 2 }) + " ГиБ";
      const date = version.createdAt == null ? "—" : new Date(version.createdAt * 1000).toLocaleString("ru-RU");
      row.append(...(index === 0 ? common : [cell(""), cell(""), cell("")]),
        versionCell, cell(size), cell(date, "when"), index === 0 ? actions : cell("", "actions"));
      rows.append(row);
    }
  }
  byId("images").replaceChildren(rows);
  byId("imageTable").hidden = catalog.length === 0;
  byId("noImages").hidden = catalog.length !== 0;
  byId("imageCount").textContent = catalog.length;
  byId("imageBadge").textContent = unavailable;
  byId("imageBadge").hidden = unavailable === 0;
}

function imageActions(image, current, busy) {
  const locked = busy ? "образ используется задачей" : "";
  const items = [];
  const older = image.versions.filter(version => !version.disabled && version !== current);
  for (const version of older)
    items.push({ text: "Откатить к " + version.file, why: locked || (version.missing ? "файл не найден" : ""),
      run: () => changeVersion(image, version, "restore") });
  if (!older.length) items.push({ text: "Откатить", why: "нет более старых версий" });
  for (const version of image.versions.filter(item => item.disabled))
    items.push({ text: "Вернуть " + version.file, why: locked || (version.missing ? "файл не найден" : ""),
      run: () => changeVersion(image, version, "enable") });
  items.push({ text: "Слить версии", why: locked || (current?.missing ? "файлы не найдены"
    : current?.depth > 1 ? "" : "в цепочке одна версия"),
    confirm: "Слить версии? Возможность отката пропадёт.",
    run: () => changeImage(image, "POST", "/merge") }, null);
  for (const version of image.versions.filter(item => !image.versions.some(newer => newer.parent === item.id)))
    items.push({ text: "Удалить " + version.file, danger: true, why: locked,
      run: () => changeVersion(image, version, "delete") });
  if (!image.versions.length)
    items.push({ text: "Удалить образ", danger: true, why: locked, confirm: "Удалить образ «" + image.name + "»?",
      run: () => changeImage(image, "DELETE", "") });
  return items;
}

async function changeImage(image, method, suffix) {
  showError();
  try {
    await requestJson("/api/images/" + image.id + suffix, { method });
    await loadImages();
  } catch (error) { showError(error); }
}


async function changeVersion(image, version, operation) {
  if (operation === "delete" && !window.confirm("Удалить " + version.file + "?")) return;
  if (operation === "restore" && !window.confirm("Развёртывание пойдёт с " + version.file + ". Откатить?")) return;
  showError();
  try {
    const path = "/api/images/" + image.id + "/versions/" + version.id;
    await requestJson(operation === "delete" ? path : path + "/" + operation,
      { method: operation === "delete" ? "DELETE" : "POST" });
    await loadImages();
  } catch (error) {
    showError(error);
  }
}
byId("imageForm").addEventListener("submit", async event => {
  event.preventDefault();
  showError();
  try {
    await requestJson("/api/images", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name: byId("newImage").value })
    });
    event.target.reset();
    await loadImages();
  } catch (error) { showError(error); }
});
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

window.addEventListener("hashchange", () => {
  route();
  if (location.hash === "#images") loadImages().catch(showError);
});
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
