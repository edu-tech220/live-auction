import { api } from "../api.js";
import { renderConnectionStatus } from "../realtime.js";
import { saveSession } from "../session.js";
import { escape, renderHeader } from "../ui.js";

renderConnectionStatus(renderHeader("login"), "offline");
document.getElementById("conn-status").hidden = true;

let mode = "login";
const $ = (id) => document.getElementById(id);

// Solo se permite volver a páginas locales del propio sitio (evita "open redirect").
function safeReturnUrl() {
  const value = new URLSearchParams(location.search).get("returnUrl") ?? "index.html";
  return /^[a-z]+\.html(\?[\w=&%-]*)?$/i.test(value) ? value : "index.html";
}

document.querySelectorAll(".tabs button").forEach((btn) => btn.addEventListener("click", () => {
  document.querySelectorAll(".tabs button").forEach((b) => b.classList.remove("active"));
  btn.classList.add("active");
  mode = btn.dataset.mode;
  const register = mode === "register";
  $("name-field").hidden = !register;
  $("password-hint").hidden = !register;
  $("password").autocomplete = register ? "new-password" : "current-password";
  $("submit").textContent = register ? "Crear cuenta" : "Entrar";
  $("message").innerHTML = "";
}));

$("form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const submit = $("submit");
  submit.disabled = true;
  $("message").innerHTML = "";

  try {
    const email = $("email").value.trim();
    const password = $("password").value;
    const auth = mode === "register"
      ? await api.register(email, $("displayName").value.trim(), password)
      : await api.login(email, password);

    saveSession(auth);
    location.href = safeReturnUrl();
  } catch (err) {
    $("message").innerHTML = `<div class="alert alert-error">${escape(err.message)}</div>`;
  } finally {
    submit.disabled = false;
  }
});
