import { button, openMenu } from "./lib/menu.js";
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
const kinds = { agent: "Проверка связи", "agent-csharp": "Проверка C#-агента", clone: "Клонирование", deploy: "Развёртывание", format: "Форматирование", "format-csharp": "Форматирование (C#)" };
const states = { queued: "В очереди", running: "Выполняется", succeeded: "Готово", failed: "Ошибка", cancelled: "Отменена" };

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
      const menu = button("Действия ▾", "", () => openMenu(menu, hostActions(computer)));
      menu.setAttribute("aria-haspopup", "menu");
      action.append(menu);
      const image = images.find(item => item.id === computer.imageId);
      const task = checks.find(item => item.mac === computer.mac);
      seen.classList.add("hide-sm");
      row.append(cell(computer.name, "name"), mac,
        cell(image?.name ?? (computer.imageId ? "Образ недоступен" : "—")), seen,
        cell(kinds[task?.kind] ?? "—"), cell(states[task?.status] ?? "—"),
        cell(task?.stage || "—", "hide-sm"), cell(task ? task.percent + "%" : "—"),
        cell(task?.message || "—", "hide-sm"), cell(task?.finishedAt
          ? new Date(task.finishedAt * 1000).toLocaleString("ru-RU") : "—", "hide-sm"), action);
      registered.append(row);
      known++;
    } else {
      const action = cell("", "actions");
      const add = document.createElement("button");
      add.textContent = "Добавить ПК";
      add.onclick = () => openComputer(computer);
      const hide = button("Скрыть", "quiet", () => removeComputer("/api/discovered/" + computer.mac));
      action.append(add, " ", hide);
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
  byId("hostCount").textContent = known;
  byId("foundCount").textContent = found;
  byId("foundBadge").textContent = found;
  byId("foundBadge").hidden = found === 0;
}

function hostActions(computer) {
  const task = checks.find(item => item.mac === computer.mac && item.finishedAt === null);
  const image = images.find(item => item.id === computer.imageId);
  const edit = { text: "Изменить", run: () => openComputer(computer) };
  const remove = { text: "Удалить", danger: true, confirm: "Удалить " + computer.name + "?",
    run: () => removeComputer("/api/hosts/" + computer.mac) };
  if (task)
    return [{ text: "Отменить задачу", run: () => cancelTask(task.id) }, null, edit, remove];

  const current = image?.versions.find(item => !item.disabled);
  const noImage = !image ? "сначала выберите образ" : current?.missing ? "файлы образа недоступны" : "";
  return [
    { text: "Клонировать", why: noImage, run: () => queueTask(computer, "clone") },
    { text: "Развернуть", why: noImage || (current ? "" : "в образе нет версий"),
      confirm: "Стереть диск " + computer.name + "?", run: () => queueTask(computer, "deploy") },
    { text: "Проверить связь", run: () => queueTask(computer, "agent") },
    { text: "Проверить C#-агент", run: () => queueTask(computer, "agent-csharp") },
    { text: "Форматировать в том WB", danger: true, confirm: "Стереть диск " + computer.name + "?",
      run: () => queueTask(computer, "format") },
    { text: "Форматировать в том WB (C#)", danger: true, confirm: "Стереть диск " + computer.name + "?",
      run: () => queueTask(computer, "format-csharp") },
    null, edit, remove
  ];
}

async function queueTask(computer, kind) {
  try {
    await requestJson("/api/hosts/" + computer.mac + "/task", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ kind, confirmed: ["deploy", "format", "format-csharp"].includes(kind) })
    });
    location.hash = "#tasks";
  } catch (error) { showError(error.message); }
}

async function cancelTask(id) {
  try { await requestJson("/api/link-checks/" + id, { method: "DELETE" }); }
  catch (error) {
    if (location.hash === "#tasks") showTaskError(error.message);
    else showError(error.message);
  }
}

async function removeComputer(path) {
  try { await requestJson(path, { method: "DELETE" }); }
  catch (error) { showError(error.message); }
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
  drawChecks();
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
  document.dispatchEvent(new CustomEvent("computers-updated", { detail: computers }));
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

const onlyErrors = byId("onlyErrors");
onlyErrors.onclick = () => {
  onlyErrors.setAttribute("aria-pressed", String(onlyErrors.getAttribute("aria-pressed") !== "true"));
  drawChecks();
};

function drawChecks() {
  const rows = document.createDocumentFragment();
  const history = document.createDocumentFragment();
  let activeCount = 0;
  let doneCount = 0;
  const showOnlyErrors = onlyErrors.getAttribute("aria-pressed") === "true";
  const date = value => new Date(value * 1000).toLocaleString("ru-RU");
  for (const check of checks) {
    const row = document.createElement("tr");
    const name = computers.find(computer => computer.mac === check.mac)?.name ?? formatMac(check.mac);
    const image = images.find(item => item.id === check.imageId);
    const action = cell("", "actions");
    row.append(cell(name, "name"), cell(kinds[check.kind] ?? check.kind), cell(image?.name ?? "—"),
      cell(states[check.status] ?? check.status));
    if (check.finishedAt === null) {
      action.append(button("Отменить", "", () => cancelTask(check.id)));
      row.append(cell(check.stage, "hide-sm"), cell(check.percent + "%"),
        cell(check.message, "hide-sm"), cell(date(check.createdAt), "hide-sm"), action);
      rows.append(row);
      activeCount++;
    } else if (!showOnlyErrors || check.status === "failed") {
      action.append(button("Метрики", "", () => showMetrics(check.id)));
      row.append(cell(check.message || check.stage), cell(date(check.createdAt), "hide-sm"),
        cell(date(check.finishedAt)), action);
      history.append(row);
      doneCount++;
    }
  }
  byId("taskBadge").textContent = activeCount;
  byId("taskBadge").hidden = activeCount === 0;
  byId("activeCount").textContent = activeCount;
  byId("doneCount").textContent = doneCount;
  byId("linkChecks").replaceChildren(rows);
  byId("linkCheckTable").hidden = activeCount === 0;
  byId("noLinkChecks").hidden = activeCount !== 0;
  byId("finishedTasks").replaceChildren(history);
  byId("finishedTable").hidden = doneCount === 0;
  byId("noFinishedTasks").hidden = doneCount !== 0;
  byId("noFinishedTasks").textContent = showOnlyErrors ? "Ошибок нет." : "Завершённых задач нет.";
}

watchEvents("/api/link-checks/events", "tasks", data => {
  byId("tasksError").hidden = true;
  checks = JSON.parse(data);
  document.dispatchEvent(new CustomEvent("tasks-updated", { detail: checks }));
  draw(computers);
  drawChecks();
}, {
  onError: () => showTaskError("Связь со списком задач потеряна. Переподключение…")
});

async function showMetrics(id) {
  try {
    const result = await requestJson("/api/tasks/" + id + "/metrics");
    const content = document.createDocumentFragment();
    const summary = document.createElement("p");
    summary.textContent = "Всего: " + (result.totalMs ?? 0) + " мс";
    content.append(summary);
    for (const [label, items, value] of [
      ["Этапы", result.steps, item => item.ms + " мс"],
      ["Операции", result.work, item => item.ms + " мс"],
      ["Объёмы", result.amounts, item => item.bytes ? item.count + " байт" : item.count]
    ]) {
      if (!items?.length) continue;
      const heading = document.createElement("h3");
      heading.textContent = label;
      const table = document.createElement("table");
      for (const item of items) table.insertRow().append(cell(item.name), cell(value(item), "num"));
      content.append(heading, table);
    }
    byId("metricsBody").replaceChildren(content);
    byId("metricsDialog").showModal();
  } catch (error) { showTaskError(error.message); }
}
function showTaskError(message) {
  byId("tasksError").textContent = message;
  byId("tasksError").hidden = false;
}
