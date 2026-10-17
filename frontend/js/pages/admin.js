import { api } from "../api.js";
import { renderConnectionStatus } from "../realtime.js";
import { isAdmin } from "../session.js";
import { escape, formatMoney, renderHeader, statusBadge, toast } from "../ui.js";

// La UI se oculta a no-admins por comodidad; la protección real es la política "Admin" de la API.
if (!isAdmin()) location.href = "login.html?returnUrl=admin.html";

const conn = renderHeader("admin");
renderConnectionStatus(conn, "offline");
conn.hidden = true;

const $ = (id) => document.getElementById(id);

// Valores por defecto: empieza en 1 minuto y dura 30.
const toLocalInput = (date) => new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
$("startsAt").value = toLocalInput(new Date(Date.now() + 60_000));
$("endsAt").value = toLocalInput(new Date(Date.now() + 31 * 60_000));

async function loadTable() {
  const page = await api.listAuctions({ pageSize: 50 });
  $("table").innerHTML = `
    <thead><tr><th>#</th><th>Título</th><th>Estado</th><th>Precio</th><th>Ofertas</th><th>Fin</th><th></th></tr></thead>
    <tbody>${page.items.map((a) => `
      <tr>
        <td>${a.id}</td>
        <td><a href="auction.html?id=${a.id}">${escape(a.title)}</a></td>
        <td>${statusBadge(a.status)}</td>
        <td>${formatMoney(a.currentPrice)}</td>
        <td>${a.bidCount}</td>
        <td class="muted small">${new Date(a.endsAt).toLocaleString("es-MX")}</td>
        <td>${a.status === "Active" || a.status === "Scheduled" ? `<button class="btn btn-danger btn-sm" data-cancel="${a.id}">Cancelar</button>` : ""}</td>
      </tr>`).join("")}
    </tbody>`;

  $("table").querySelectorAll("[data-cancel]").forEach((btn) => btn.addEventListener("click", async () => {
    if (!confirm("¿Cancelar esta subasta? Nadie podrá ganarla.")) return;
    try {
      await api.cancelAuction(btn.dataset.cancel);
      toast("Subasta cancelada.", "success");
      await loadTable();
    } catch (err) {
      toast(err.message, "error");
    }
  }));
}

$("form").addEventListener("submit", async (event) => {
  event.preventDefault();
  $("message").innerHTML = "";
  try {
    const created = await api.createAuction({
      title: $("title").value.trim(),
      description: $("description").value.trim() || null,
      imageUrl: $("imageUrl").value.trim() || null,
      startingPrice: Number($("startingPrice").value),
      minIncrement: Number($("minIncrement").value),
      // datetime-local no tiene zona: se interpreta como hora local y se manda en ISO UTC.
      startsAt: new Date($("startsAt").value).toISOString(),
      endsAt: new Date($("endsAt").value).toISOString()
    });
    $("message").innerHTML = `<div class="alert alert-ok">Subasta #${created.id} creada.</div>`;
    event.target.reset();
    await loadTable();
  } catch (err) {
    $("message").innerHTML = `<div class="alert alert-error">${escape(err.message)}</div>`;
  }
});

await loadTable();
