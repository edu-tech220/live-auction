// Configuración del frontend. La URL de la API es lo único que cambia entre entornos.
// En Docker Compose la API se publica en el mismo puerto, así que este valor sirve igual.
export const config = {
  apiBaseUrl: window.LIVE_AUCTION_API ?? "http://localhost:5080",
  hubPath: "/hubs/auctions",
  locale: "es-MX",
  currency: "MXN"
};
