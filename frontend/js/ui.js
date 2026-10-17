import { config } from "./config.js";
import { clearSession, currentUser, isAdmin } from "./session.js";

const money = new Intl.NumberFormat(config.locale, { style: "currency", currency: config.currency });
export const formatMoney = (value) => money.format(Number(value));

export const formatTime = (iso) =>
  new Date(iso).toLocaleString(config.locale, { day: "2-digit", month: "short", hour: "2-digit", minute: "2-digit", second: "2-digit" });

// Escapa texto antes de insertarlo como HTML: títulos y nombres vienen de usuarios (anti-XSS).
export function escape(value) {
  return String(value ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

const STATUS = {
  Active: { label: "En vivo", css: "badge-active" },
  Scheduled: { label: "Próximamente", css: "badge-scheduled" },
  Finished: { label: "Finalizada", css: "badge-finished" },
  Cancelled: { label: "Cancelada", css: "badge-cancelled" }
};

export const statusBadge = (status) => {
  const s = STATUS[status] ?? { label: status, css: "" };
  return `<span class="badge ${s.css}">${s.label}</span>`;
};

// Cuenta regresiva SOLO visual. offsetMs = horaServidor - horaCliente: corrige relojes
// desajustados. La decisión real de si se puede pujar la toma siempre el servidor.
export function remainingText(targetIso, offsetMs) {
  const ms = new Date(targetIso).getTime() - (Date.now() + offsetMs);
  if (ms <= 0) return { text: "00:00:00", ms: 0 };
  const s = Math.floor(ms / 1000);
  const d = Math.floor(s / 86400);
  const hh = String(Math.floor((s % 86400) / 3600)).padStart(2, "0");
  const mm = String(Math.floor((s % 3600) / 60)).padStart(2, "0");
  const ss = String(s % 60).padStart(2, "0");
  return { text: d > 0 ? `${d}d ${hh}:${mm}:${ss}` : `${hh}:${mm}:${ss}`, ms };
}

export function toast(message, type = "info") {
  let host = document.querySelector(".toasts");
  if (!host) {
    host = document.createElement("div");
    host.className = "toasts";
    document.body.appendChild(host);
  }
  const el = document.createElement("div");
  el.className = `toast ${type}`;
  el.textContent = message;
  host.appendChild(el);
  setTimeout(() => el.remove(), 4500);
}

export function flash(element) {
  if (!element) return;
  element.classList.remove("flash");
  void element.offsetWidth; // reinicia la animación
  element.classList.add("flash");
}

const initials = (name) => name.split(" ").filter(Boolean).slice(0, 2).map((p) => p[0].toUpperCase()).join("");

// Header común: marca, navegación, usuario e indicador de conexión.
export function renderHeader(active) {
  const user = currentUser();
  const header = document.querySelector("header.topbar");
  header.innerHTML = `
    <div class="container">
      <a class="brand" href="index.html"><span class="brand-mark">⚡</span> Live Auction</a>
      <nav class="nav">
        <a href="index.html" class="${active === "home" ? "active" : ""}">Subastas</a>
        ${isAdmin() ? `<a href="admin.html" class="${active === "admin" ? "active" : ""}">Administrar</a>` : ""}
      </nav>
      <div class="spacer"></div>
      <span class="conn" id="conn-status">Conectando…</span>
      ${user
        ? `<div class="user-chip"><span>${escape(user.displayName)}</span><span class="avatar">${escape(initials(user.displayName))}</span>
             <button class="btn btn-ghost btn-sm" id="logout" title="Cerrar sesión">Salir</button></div>`
        : `<a class="btn btn-gold btn-sm" href="login.html?returnUrl=${encodeURIComponent(location.pathname.split("/").pop() + location.search)}">Iniciar sesión</a>`}
    </div>`;
  header.querySelector("#logout")?.addEventListener("click", () => { clearSession(); location.reload(); });
  return header.querySelector("#conn-status");
}
