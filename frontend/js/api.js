import { config } from "./config.js";
import { accessToken, clearSession } from "./session.js";

// Error de la API con el "code" estable de ProblemDetails (p. ej. "BidTooLow").
export class ApiError extends Error {
  constructor(status, code, message, errors) {
    super(message);
    this.status = status;
    this.code = code;
    this.errors = errors;
  }
}

async function request(path, { method = "GET", body } = {}) {
  const headers = { Accept: "application/json" };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const token = accessToken();
  if (token) headers.Authorization = `Bearer ${token}`;

  let response;
  try {
    response = await fetch(config.apiBaseUrl + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  } catch {
    throw new ApiError(0, "NetworkError", "No se pudo conectar con el servidor.");
  }

  if (response.status === 204) return null;

  const isJson = response.headers.get("content-type")?.includes("json");
  const data = isJson ? await response.json() : null;

  if (!response.ok) {
    if (response.status === 401 && token) clearSession(); // token vencido o inválido
    const fieldErrors = data?.errors ? Object.values(data.errors).flat().join(" ") : null;
    throw new ApiError(response.status, data?.code ?? `Http${response.status}`,
      fieldErrors || data?.detail || data?.title || "Ocurrió un error.", data?.errors);
  }

  return data;
}

export const api = {
  login: (email, password) => request("/api/auth/login", { method: "POST", body: { email, password } }),
  register: (email, displayName, password) => request("/api/auth/register", { method: "POST", body: { email, displayName, password } }),
  me: () => request("/api/auth/me"),

  listAuctions: ({ status, search, page = 1, pageSize = 12 } = {}) => {
    const q = new URLSearchParams({ page, pageSize });
    if (status) q.set("status", status);
    if (search) q.set("search", search);
    return request(`/api/auctions?${q}`);
  },
  getAuction: (id) => request(`/api/auctions/${id}`),
  createAuction: (payload) => request("/api/auctions", { method: "POST", body: payload }),
  cancelAuction: (id) => request(`/api/auctions/${id}/cancel`, { method: "POST" }),

  getBids: (id, page = 1, pageSize = 50) => request(`/api/auctions/${id}/bids?page=${page}&pageSize=${pageSize}`),
  // El frontend solo manda el monto: usuario y precio vigente los determina el servidor.
  placeBid: (id, amount) => request(`/api/auctions/${id}/bids`, { method: "POST", body: { amount } }),
  myBids: () => request("/api/me/bids")
};
