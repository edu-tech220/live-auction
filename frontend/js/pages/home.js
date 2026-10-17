import { api } from "../api.js";
import { createConnection, renderConnectionStatus } from "../realtime.js";
import { escape, flash, formatMoney, remainingText, renderHeader, statusBadge, toast } from "../ui.js";

const grid = document.getElementById("grid");
const search = document.getElementById("search");
const connStatus = renderHeader("home");

let status = "Active";
let auctions = new Map(); // id -> resumen (para actualizar tarjetas en vivo)

const PLACEHOLDER = "data:image/svg+xml;utf8," + encodeURIComponent(
  `<svg xmlns='http://www.w3.org/2000/svg' width='400' height='300'><rect width='100%' height='100%' fill='#1d2230'/><text x='50%' y='52%' fill='#8b93a7' font-family='sans-serif' font-size='22' text-anchor='middle'>Sin imagen</text></svg>`);

function cardHtml(a) {
  const target = a.status === "Scheduled" ? a.startsAt : a.endsAt;
  const label = a.status === "Scheduled" ? "Empieza en" : a.status === "Active" ? "Termina en" : "Terminó";
  return `
    <a class="card" href="auction.html?id=${a.id}" data-id="${a.id}">
      <div class="card-media">
        <img src="${escape(a.imageUrl || PLACEHOLDER)}" alt="" loading="lazy" />
        <span class="badge-slot">${statusBadge(a.status)}</span>
      </div>
      <div class="card-body">
        <h3 class="card-title">${escape(a.title)}</h3>
        <div class="card-row">
          <div>
            <div class="price-label">${a.bidCount > 0 ? "Oferta actual" : "Precio inicial"}</div>
            <div class="price" data-role="price">${formatMoney(a.currentPrice)}</div>
          </div>
          <div style="text-align:right">
            <div class="price-label">${label}</div>
            <div class="countdown" data-role="countdown" data-target="${target}" data-live="${a.status === "Active" || a.status === "Scheduled"}">
              ${a.status === "Active" || a.status === "Scheduled" ? "" : new Date(a.endsAt).toLocaleDateString("es-MX")}
            </div>
          </div>
        </div>
        <div class="muted small" data-role="bids">${a.bidCount} ${a.bidCount === 1 ? "oferta" : "ofertas"}</div>
      </div>
    </a>`;
}

async function load() {
  try {
    const page = await api.listAuctions({ status, search: search.value.trim(), pageSize: 48 });
    auctions = new Map(page.items.map((a) => [a.id, a]));
    grid.innerHTML = page.items.length
      ? page.items.map(cardHtml).join("")
      : `<div class="empty" style="grid-column:1/-1">No hay subastas en esta categoría.</div>`;
    // Imagen rota => placeholder (listener en vez de onerror en línea).
    grid.querySelectorAll("img").forEach((img) => img.addEventListener("error", () => { img.src = PLACEHOLDER; }, { once: true }));
    tick();
  } catch (err) {
    grid.innerHTML = `<div class="empty" style="grid-column:1/-1">${escape(err.message)}</div>`;
  }
}

function tick() {
  grid.querySelectorAll('[data-role="countdown"][data-live="true"]').forEach((el) => {
    const { text, ms } = remainingText(el.dataset.target, 0);
    el.textContent = text;
    el.classList.toggle("urgent", ms > 0 && ms < 60_000);
  });
}

// ---------------- Tiempo real ----------------

const { connection, start } = createConnection({
  onStatus: (s) => renderConnectionStatus(connStatus, s)
});

// Cambio de precio en cualquier subasta: actualiza su tarjeta sin recargar.
connection.on("BidPlaced", (e) => {
  const a = auctions.get(e.auctionId);
  if (!a || e.bidCount <= a.bidCount) return; // evento viejo o desordenado: ignorar
  a.currentPrice = e.amount;
  a.bidCount = e.bidCount;
  const card = grid.querySelector(`[data-id="${e.auctionId}"]`);
  if (!card) return;
  const price = card.querySelector('[data-role="price"]');
  price.textContent = formatMoney(e.amount);
  card.querySelector('[data-role="bids"]').textContent = `${e.bidCount} ${e.bidCount === 1 ? "oferta" : "ofertas"}`;
  flash(price);
});

connection.on("AuctionStatusChanged", () => load());
connection.on("AuctionFinished", (e) => {
  const a = auctions.get(e.auctionId);
  if (a) toast(`"${a.title}" terminó${e.winnerName ? ` · ganó ${e.winnerName} con ${formatMoney(e.finalPrice)}` : " sin ofertas"}`, "info");
  load();
});

// Al reconectar se pierden los grupos: volver al lobby y resincronizar la lista.
connection.onreconnected(async () => {
  await connection.invoke("JoinLobby");
  await load();
});

// ---------------- Filtros ----------------

document.querySelectorAll(".tabs button").forEach((btn) => btn.addEventListener("click", () => {
  document.querySelectorAll(".tabs button").forEach((b) => b.classList.remove("active"));
  btn.classList.add("active");
  status = btn.dataset.status;
  load();
}));

let debounce;
search.addEventListener("input", () => {
  clearTimeout(debounce);
  debounce = setTimeout(load, 300);
});

await load();
setInterval(tick, 1000);
start().then(() => connection.invoke("JoinLobby")).catch(() => { /* reintenta solo */ });
