import { requestJson } from "./lib/http.js";
import { watchEvents } from "./lib/events.js";

const byId = id => document.getElementById(id);
const section = byId("devSection");
const select = byId("scenarioHost");
const start = byId("scenarioStart");
const stop = byId("scenarioStop");

const labels = { running: "Выполняется", waiting: "Ожидает", done: "Готово", failed: "Ошибка", stopped: "Остановлен" };

function showError(error) {
  byId("tasksError").textContent = error.message;
  byId("tasksError").hidden = false;
}

async function fillHosts() {
  const selected = select.value;
  const hosts = await requestJson("/api/hosts");
  select.replaceChildren(new Option("Выберите ПК", ""));
  for (const host of hosts.filter(host => host.name && host.imageId))
    select.add(new Option(host.name, host.mac));
  if ([...select.options].some(option => option.value === selected)) select.value = selected;
}

function draw(runs) {
  start.disabled = runs[0]?.status === "running";
  stop.hidden = !start.disabled;
  const results = document.createDocumentFragment();
  for (const run of runs) {
    const row = document.createElement("div");
    row.className = "scenario-run";
    const title = document.createElement("b");
    title.textContent = run.host + " · " + labels[run.status];
    const steps = document.createElement("ol");
    for (const step of run.steps) {
      const item = document.createElement("li");
      item.textContent = step.name + " · " + step.image + " · " + labels[step.status];
      steps.append(item);
    }
    row.append(title, steps);
    if (run.error) {
      const error = document.createElement("p");
      error.className = "err";
      error.textContent = run.error;
      row.append(error);
    }
    results.append(row);
  }
  byId("scenarioRuns").replaceChildren(results);
}

try {
  if ((await requestJson("/api/scenarios")).enabled) {
    section.hidden = false;
    const refresh = () => {
      if (location.hash === "#tasks") fillHosts().catch(showError);
    };
    window.addEventListener("hashchange", refresh);
    refresh();
    watchEvents("/api/scenarios/events", "scenarios", data => draw(JSON.parse(data)), {
      active: () => location.hash === "#tasks",
      onError: () => showError(new Error("Связь со сценарием потеряна."))
    });
    byId("scenarioForm").onsubmit = async event => {
      event.preventDefault();
      if (!select.value || !window.confirm("Стереть диск тестового ПК и запустить сценарий?")) return;
      try {
        await requestJson("/api/scenarios", {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ mac: select.value, confirmed: true })
        });
      } catch (error) { showError(error); }
    };
    stop.onclick = async () => {
      try { await requestJson("/api/scenarios", { method: "DELETE" }); }
      catch (error) { showError(error); }
    };
  }
} catch (error) { showError(error); }
