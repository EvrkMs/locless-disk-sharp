import { watchEvents } from "./lib/events.js";
import { requestJson } from "./lib/http.js";

const byId = id => document.getElementById(id);
const dialog = byId("registerHost");
const form = byId("registerHostForm");
const submit = form.querySelector('button[type="submit"]');
let selectedMac;
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
    const seen = cell(new Date(computer.lastSeen * 1000).toLocaleString("ru-RU"), "when");
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
      action.append(button);
      row.append(cell(computer.name, "name"), mac, seen, action);
      registered.append(row);
      known++;
    } else {
      const action = cell("", "actions");
      const add = document.createElement("button");
      add.textContent = "Добавить ПК";
      add.onclick = () => {
        selectedMac = computer.mac;
        byId("hostName").value = "";
        byId("registerMac").textContent = formatMac(selectedMac);
        byId("registerError").textContent = "";
        dialog.showModal();
      };
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

byId("cancelRegister").onclick = () => dialog.close();
form.onsubmit = async event => {
  event.preventDefault();
  submit.disabled = true;
  try {
    await requestJson("/api/hosts/" + selectedMac + "/register", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name: byId("hostName").value })
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
          showError(error.message);
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
  checks = JSON.parse(data);
  draw(computers);
  drawChecks();
}, {
  active: () => location.hash === "#pcs",
  onError: () => showError("Связь со списком проверок потеряна. Переподключение…")
});
