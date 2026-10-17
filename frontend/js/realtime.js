import { config } from "./config.js";
import { accessToken } from "./session.js";

// Conexión SignalR compartida por la página.
// - accessTokenFactory: en WebSockets el token viaja como ?access_token= (el navegador no permite
//   encabezados en WebSockets); el servidor solo lo acepta en la ruta del Hub.
// - withAutomaticReconnect: reintenta 0s, 2s, 5s, 10s, 30s. Al reconectar se pierde la
//   membresía de grupos, así que cada página vuelve a unirse y se resincroniza (onReconnected).
export function createConnection({ onStatus } = {}) {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(config.apiBaseUrl + config.hubPath, { accessTokenFactory: () => accessToken() ?? "" })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  const report = (state) => onStatus?.(state);
  connection.onreconnecting(() => report("reconnecting"));
  connection.onreconnected(() => report("online"));
  connection.onclose(() => report("offline"));

  return {
    connection,
    async start() {
      try {
        await connection.start();
        report("online");
      } catch (err) {
        report("offline");
        // Reintento manual del primer arranque (withAutomaticReconnect solo cubre caídas posteriores).
        setTimeout(() => this.start(), 5000);
        throw err;
      }
    }
  };
}

// Pinta el indicador de conexión del header.
export function renderConnectionStatus(element, state) {
  if (!element) return;
  const labels = { online: "En vivo", reconnecting: "Reconectando…", offline: "Sin conexión" };
  element.className = `conn ${state}`;
  element.textContent = labels[state] ?? state;
}
