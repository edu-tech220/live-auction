// Sesión del usuario en el navegador.
//
// El JWT se guarda en localStorage por simplicidad (SPA sin backend propio). Riesgo conocido:
// si la página tuviera una vulnerabilidad XSS, un script podría leerlo. Mitigaciones aplicadas:
// nunca se inserta HTML de usuarios sin escapar (ver ui.escape) y el token expira. En producción
// se podría usar una cookie HttpOnly emitida por un BFF. Ver docs/decisions.md.

const KEY = "live-auction.session";

export function getSession() {
  try {
    const raw = localStorage.getItem(KEY);
    if (!raw) return null;
    const session = JSON.parse(raw);
    // Token vencido => se descarta (el servidor lo rechazaría de todos modos).
    if (new Date(session.expiresAt) <= new Date()) {
      localStorage.removeItem(KEY);
      return null;
    }
    return session;
  } catch {
    return null;
  }
}

export function saveSession(auth) {
  localStorage.setItem(KEY, JSON.stringify({ token: auth.accessToken, expiresAt: auth.expiresAt, user: auth.user }));
}

export function clearSession() {
  localStorage.removeItem(KEY);
}

export const currentUser = () => getSession()?.user ?? null;
export const accessToken = () => getSession()?.token ?? null;
export const isAdmin = () => currentUser()?.role === "Admin";
