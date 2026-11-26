import { watchEvents } from "./lib/events.js";
import { requestJson } from "./lib/http.js";

const byId = id => document.getElementById(id);
const dialog = byId("registerHost");
const form = byId("registerHostForm");
const submit = form.querySelector('button[type="submit"]');
let editedComputer;
let editorRequest = 0;
let images = [];
let computers = [];
let checks = [];

function formatMac(mac) {
  return mac.match(/../g).join(":").toLowerCase();
}

function cell(value, className) {
  const td = document.createElement("td");
  td.textContent = value;
  if (className) td.className = className;
  return td;
}

function draw(computers) {
  const registered = document.createDocumentFragment();
  const discovered = document.createDocumentFragment();
  let known = 0;
  let found = 0;
  for (const computer of computers) {
    const row = document.createElement("tr");
    const mac = cell(formatMac(computer.mac), "mac");
    const seen = cell(computer.lastSeen ? new Date(computer.lastSeen * 1000).toLocaleString("ru-RU") : "—", "when");
    if (computer.name !== null) {
      const action = cell("", "actions");
      const button = document.createElement("button");
      button.textContent = "Проверить связь";
      button.disabled = checks.some(check => check.mac === computer.mac && check.finishedAt === null);
      button.onclick = async () => {
        button.disabled = true;
        try {
          await requestJson("/api/link-checks/" + computer.mac, { method: "POST" });
        } catch (error) {
          button.disabled = false;
          showError(error.message);
        }
      };
      const edit = document.createElement("button");
      edit.textContent = "Настроить";
      edit.onclick = () => openComputer(computer);
      action.append(edit, " ", button);
      const image = images.find(item => item.id === computer.imageId);
      row.append(cell(computer.name, "name"), mac,
        cell(image?.name ?? (computer.imageId ? "Образ недоступен" : "—")), seen, action);
      registered.append(row);
      known++;
    } else {
      const action = cell("", "actions");
      const add = document.createElement("button");
      add.textContent = "Добавить ПК";
      add.onclick = () => openComputer(computer);
      action.append(add);
      row.append(mac, seen, action);
      discovered.append(row);
      found++;
    }
  }
  byId("hosts").replaceChildren(registered);
  byId("found").replaceChildren(discovered);
  byId("hostTable").hidden = known === 0;
  byId("foundTable").hidden = found === 0;
  byId("noHosts").hidden = known !== 0;
  byId("noFound").hidden = found !== 0;
  byId("foundCount").textContent = found;
  byId("foundBadge").textContent = found;
  byId("foundBadge").hidden = found === 0;
}

async function openComputer(computer) {
  const request = ++editorRequest;
  editedComputer = computer?.name ? computer : undefined;
  byId("registerHostTitle").textContent = editedComputer ? "Настроить ПК" : "Добавить ПК";
  byId("hostSave").textContent = editedComputer ? "Сохранить" : "Добавить";
  byId("hostMac").value = computer ? formatMac(computer.mac) : "";
  byId("hostMac").readOnly = Boolean(computer);
  byId("hostName").value = computer?.name ?? "";
  byId("registerError").textContent = "";
  const select = byId("hostImage");
  select.replaceChildren(new Option("Без образа", ""));
  submit.disabled = true;
  dialog.showModal();
  try {
    const catalog = await requestJson("/api/images");
    if (request !== editorRequest || !dialog.open) return;
    images = catalog;
    for (const image of images) select.add(new Option(image.name, image.id));
    if (computer?.imageId && !images.some(image => image.id === computer.imageId))
      select.add(new Option("Текущий образ недоступен", computer.imageId));
    select.value = computer?.imageId ?? "";
    submit.disabled = false;
  } catch (error) {
    if (request === editorRequest && dialog.open) byId("registerError").textContent = error.message;
  }
}

byId("addHost").onclick = () => openComputer();
byId("cancelRegister").onclick = () => dialog.close();
document.addEventListener("images-updated", event => {
  images = event.detail;
  draw(computers);
});

form.onsubmit = async event => {
  event.preventDefault();
  submit.disabled = true;
  try {
    await requestJson(editedComputer ? "/api/hosts/" + editedComputer.mac : "/api/hosts", {
      method: editedComputer ? "PUT" : "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ mac: byId("hostMac").value, name: byId("hostName").value,
        imageId: byId("hostImage").value || null })
    });
    dialog.close();
  } catch (error) {
    byId("registerError").textContent = error.message;
  } finally {
    submit.disabled = false;
  }
};

watchEvents("/api/hosts/events", "hosts", data => {
  computers = JSON.parse(data);
  draw(computers);
  drawChecks();
  byId("hostsError").hidden = true;
}, {
  onError() {
    byId("hostsError").textContent = "Связь со списком ПК потеряна. Переподключение…";
    byId("hostsError").hidden = false;
  }
});

function showError(message) {
  byId("hostsError").textContent = message;
  byId("hostsError").hidden = false;
}

function drawChecks() {
  const labels = { queued: "В очереди", running: "Выполняется", succeeded: "Связь есть",
    failed: "Ошибка", cancelled: "Отменена" };
  const rows = document.createDocumentFragment();
  for (const check of checks) {
    const row = document.createElement("tr");
    const name = computers.find(computer => computer.mac === check.mac)?.name ?? formatMac(check.mac);
    const action = cell("", "actions");
    if (check.finishedAt === null) {
      const cancel = document.createElement("button");
      cancel.textContent = "Отменить";
      cancel.onclick = async () => {
        cancel.disabled = true;
        try {
          await requestJson("/api/link-checks/" + check.id, { method: "DELETE" });
        } catch (error) {
          cancel.disabled = false;
          showTaskError(error.message);
        }
      };
      action.append(cancel);
    }
    row.append(cell(name, "name"), cell(labels[check.status] ?? check.status),
      cell(check.percent + "%"), cell(check.message || check.stage), action);
    rows.append(row);
  }
  byId("linkChecks").replaceChildren(rows);
  byId("linkCheckTable").hidden = checks.length === 0;
  byId("noLinkChecks").hidden = checks.length > 0;
}

watchEvents("/api/link-checks/events", "tasks", data => {
  byId("tasksError").hidden = true;
  checks = JSON.parse(data);
  draw(computers);
  drawChecks();
}, {
  active: () => ["", "#pcs", "#tasks"].includes(location.hash),
  onError: () => showTaskError("Связь со списком проверок потеряна. Переподключение…")
});

function showTaskError(message) {
  byId("tasksError").textContent = message;
  byId("tasksError").hidden = false;
}
