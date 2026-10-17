import { api, ApiError } from "../api.js";
import { createConnection, renderConnectionStatus } from "../realtime.js";
import { currentUser } from "../session.js";
import { escape, flash, formatMoney, formatTime, remainingText, renderHeader, statusBadge, toast } from "../ui.js";

const id = Number(new URLSearchParams(location.search).get("id"));
const connStatus = renderHeader("auction");
const me = currentUser();
const $ = (sel) => document.querySelector(sel);

// Estado local = espejo del servidor. Nunca se calcula aquí si una puja es válida.
const state = { auction: null, offsetMs: 0, bids: [] };

// ---------------- Render ----------------

function applySnapshot(a) {
  state.auction = a;
  // Desfase del reloj local respecto al servidor (solo para la cuenta regresiva visual).
  state.offsetMs = new Date(a.serverTime).getTime() - Date.now();

  document.title = `${a.title} · Live Auction`;
  $("#title").textContent = a.title;
  $("#description").textContent = a.description ?? "";
  $("#seller").textContent = `Vendedor: ${a.sellerName}`;
  $("#image").src = a.imageUrl || "";
  $("#increment").textContent = formatMoney(a.minIncrement);
  renderPrice(false);
  renderStatus();
}

function renderPrice(animate) {
  const a = state.auction;
  $("#price-label").textContent = a.bidCount > 0 ? "Oferta actual" : "Precio inicial";
  $("#price").textContent = formatMoney(a.currentPrice);
  $("#bid-count").textContent = a.bidCount;
  if (animate) flash($("#price"));

  const leader = $("#leader");
  if (a.highestBidderName && a.status !== "Finished") {
    leader.hidden = false;
    leader.textContent = a.highestBidderId === me?.id ? "🏆 ¡Vas ganando!" : `Mejor postor: ${a.highestBidderName}`;
  } else {
    leader.hidden = true;
  }
  renderBidArea();
}

function renderStatus() {
  const a = state.auction;
  $("#status").innerHTML = statusBadge(a.status);
  $("#countdown-label").textContent = a.status === "Scheduled" ? "Empieza en" : "Termina en";

  const result = $("#result");
  if (a.status === "Finished") {
    result.hidden = false;
    const iWon = a.winnerId != null && a.winnerId === me?.id;
    result.innerHTML = `<div class="result">
        <div class="muted small">Subasta finalizada</div>
        ${a.winnerId == null
          ? `<h2>Sin ganador</h2><div class="muted">Nadie ofertó en esta subasta.</div>`
          : `<h2>${iWon ? "🎉 ¡Ganaste!" : `Ganó ${escape(a.winnerName)}`}</h2>
             <div class="price-xl" style="font-size:2rem">${formatMoney(a.finalPrice)}</div>`}
      </div>`;
  } else if (a.status === "Cancelled") {
    result.hidden = false;
    result.innerHTML = `<div class="result" style="border-color:rgba(248,113,113,.4);background:rgba(248,113,113,.08)"><h2>Subasta cancelada</h2></div>`;
  } else {
    result.hidden = true;
  }
  renderBidArea();
}

function renderBidArea() {
  const a = state.auction;
  const area = $("#bid-area");
  if (!a) return;

  if (a.status === "Finished" || a.status === "Cancelled") {
    area.innerHTML = "";
    return;
  }
  if (!me) {
    area.innerHTML = `<a class="btn btn-gold btn-block" href="login.html?returnUrl=${encodeURIComponent(`auction.html?id=${id}`)}">Inicia sesión para pujar</a>`;
    return;
  }
  if (me.id === a.sellerId) {
    area.innerHTML = `<div class="muted small">Eres el vendedor de esta subasta: no puedes pujar.</div>`;
    return;
  }

  // Sugerencias calculadas con el mínimo que INFORMÓ el servidor (no se inventa un precio).
  // novalidate: el navegador no bloquea montos bajos; quien acepta o rechaza es el servidor.
  const min = Number(a.minimumNextBid);
  const inc = Number(a.minIncrement);
  const current = document.getElementById("amount");
  const keepTyped = current && Number(current.value) >= min ? current.value : min.toFixed(2);

  area.innerHTML = `
    <form class="bid-form" id="bid-form" novalidate>
      <div class="quick">
        ${[min, min + inc, min + inc * 2].map((v) => `<button type="button" class="btn btn-sm" data-amount="${v.toFixed(2)}">${formatMoney(v)}</button>`).join("")}
      </div>
      <div class="amount-input">
        <span class="muted">$</span>
        <input id="amount" type="number" step="0.01" min="${min.toFixed(2)}" value="${keepTyped}" aria-label="Monto" />
        <button class="btn btn-gold" id="bid-btn" type="submit">Pujar</button>
      </div>
      <div class="muted small">Oferta mínima: <b>${formatMoney(min)}</b></div>
    </form>`;

  area.querySelectorAll("[data-amount]").forEach((b) => b.addEventListener("click", () => { $("#amount").value = b.dataset.amount; }));
  $("#bid-form").addEventListener("submit", onBid);
}

function renderHistory(newBidId) {
  const list = $("#history");
  if (state.bids.length === 0) {
    list.innerHTML = `<li class="muted small">Todavía no hay ofertas. ¡Sé el primero!</li>`;
    return;
  }
  list.innerHTML = state.bids.map((b, i) => `
    <li class="${i === 0 ? "top" : ""} ${b.id === newBidId ? "new" : ""}">
      <div><b>${escape(b.bidderName)}</b>${b.bidderId === me?.id ? ' <span class="muted small">(tú)</span>' : ""}
        <div class="muted small">${formatTime(b.placedAt)}</div></div>
      <b>${formatMoney(b.amount)}</b>
    </li>`).join("");
}

function tick() {
  const a = state.auction;
  if (!a || a.status === "Finished" || a.status === "Cancelled") {
    $("#countdown").textContent = "—";
    return;
  }
  const target = a.status === "Scheduled" ? a.startsAt : a.endsAt;
  const { text, ms } = remainingText(target, state.offsetMs);
  $("#countdown").textContent = text;
  $("#countdown").classList.toggle("urgent", ms > 0 && ms < 60_000);

  // Llegó la hora de fin: se deshabilita el botón y se ESPERA el veredicto del servidor
  // (evento AuctionFinished). El cliente no declara ganador por su cuenta.
  const btn = document.getElementById("bid-btn");
  if (btn && a.status === "Active" && ms === 0) {
    btn.disabled = true;
    btn.textContent = "Cerrando…";
  }
}

// ---------------- Pujar ----------------

async function onBid(event) {
  event.preventDefault();
  const btn = $("#bid-btn");
  const amount = Number($("#amount").value);
  if (!(amount > 0)) return toast("Escribe un monto válido.", "error");

  btn.disabled = true;
  try {
    if (connection.state === signalR.HubConnectionState.Connected) {
      // Por el Hub: si se rechaza, el servidor manda BidRejected solo a esta conexión.
      const outcome = await connection.invoke("PlaceBid", id, amount);
      if (outcome.accepted) toast(`Oferta de ${formatMoney(amount)} registrada.`, "success");
    } else {
      // Respaldo REST si el WebSocket está caído: misma regla en el servidor.
      await api.placeBid(id, amount);
      toast(`Oferta de ${formatMoney(amount)} registrada.`, "success");
      await refresh();
    }
  } catch (err) {
    toast(err instanceof ApiError ? err.message : "No se pudo enviar la oferta.", "error");
  } finally {
    btn.disabled = false;
  }
}

// ---------------- Datos ----------------

async function refresh() {
  const [details, bids] = await Promise.all([api.getAuction(id), api.getBids(id)]);
  applySnapshot(details);
  state.bids = bids.items;
  renderHistory();
}

// ---------------- Tiempo real ----------------

const { connection, start } = createConnection({
  onStatus: (s) => renderConnectionStatus(connStatus, s)
});

connection.on("BidPlaced", (e) => {
  const a = state.auction;
  if (!a || e.auctionId !== id || e.bidCount <= a.bidCount) return; // duplicado o desordenado

  const wasLeader = a.highestBidderId === me?.id;
  Object.assign(a, {
    currentPrice: e.amount, bidCount: e.bidCount, minimumNextBid: e.minimumNextBid,
    highestBidderId: e.bidderId, highestBidderName: e.bidderName, status: "Active"
  });
  state.bids.unshift({ id: e.bidId, amount: e.amount, bidderId: e.bidderId, bidderName: e.bidderName, placedAt: e.placedAt });

  renderPrice(true);
  renderStatus();
  renderHistory(e.bidId);
  if (wasLeader && e.bidderId !== me?.id) toast(`${e.bidderName} superó tu oferta: ${formatMoney(e.amount)}`, "error");
});

connection.on("BidRejected", (e) => {
  if (e.auctionId === id) toast(e.message, "error");
});

connection.on("ViewersChanged", (e) => {
  if (e.auctionId === id) $("#viewers").textContent = `👁 ${e.viewers} ${e.viewers === 1 ? "persona viendo" : "personas viendo"}`;
});

connection.on("AuctionStatusChanged", (e) => {
  if (e.auctionId !== id) return;
  state.auction.status = e.status;
  renderStatus();
  if (e.status === "Active") toast("¡La subasta comenzó!", "info");
});

connection.on("AuctionFinished", (e) => {
  if (e.auctionId !== id) return;
  Object.assign(state.auction, {
    status: e.status, winnerId: e.winnerId, winnerName: e.winnerName, finalPrice: e.finalPrice, closedAt: e.closedAt
  });
  renderPrice(false);
  renderStatus();
});

// Reconexión: el servidor olvidó los grupos de la conexión anterior. Volver a unirse y
// resincronizar todo desde la fuente de verdad (pudieron pasar pujas mientras tanto).
connection.onreconnected(async () => {
  const snapshot = await connection.invoke("JoinAuction", id);
  applySnapshot(snapshot);
  state.bids = (await api.getBids(id)).items;
  renderHistory();
});

// ---------------- Inicio ----------------

if (!Number.isInteger(id) || id <= 0) {
  document.getElementById("layout").innerHTML = `<div class="empty" style="grid-column:1/-1">Subasta inválida.</div>`;
} else {
  try {
    await refresh();
  } catch (err) {
    document.getElementById("layout").innerHTML = `<div class="empty" style="grid-column:1/-1">${escape(err.message)}</div>`;
  }
  setInterval(tick, 1000);
  tick();
  start()
    .then(async () => applySnapshot(await connection.invoke("JoinAuction", id)))
    .catch(() => { /* reintenta solo; mientras tanto la página funciona por REST */ });
}
