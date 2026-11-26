import { watchEvents } from "./lib/events.js";
import { requestJson } from "./lib/http.js";

const byId = id => document.getElementById(id);
const dialog = byId("registerHost");
const form = byId("registerHostForm");
const submit = form.querySelector('button[type="submit"]');
let selectedMac;

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
      row.append(cell(computer.name, "name"), mac, seen);
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
  draw(JSON.parse(data));
  byId("hostsError").hidden = true;
}, {
  onError() {
    byId("hostsError").textContent = "Связь со списком ПК потеряна. Переподключение…";
    byId("hostsError").hidden = false;
  }
});
