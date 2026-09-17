// code-wicket.dev is canonical; the other hostnames the Worker is attached to redirect to it.
// Aliases are listed rather than inferred, so `wrangler dev` on localhost serves the page.
const CANONICAL_HOST = "code-wicket.dev";
const ALIAS_HOSTS = new Set(["www.code-wicket.dev", "code-wicket.com", "www.code-wicket.com"]);

// The page has no scripts, no forms and no third-party resources, so the policy can be strict.
const SECURITY_HEADERS = {
  "Content-Security-Policy":
    "default-src 'none'; style-src 'self'; img-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
  "Strict-Transport-Security": "max-age=31536000",
  "X-Content-Type-Options": "nosniff",
  "Referrer-Policy": "strict-origin-when-cross-origin",
  "Permissions-Policy": "camera=(), microphone=(), geolocation=(), interest-cohort=()",
};

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (ALIAS_HOSTS.has(url.hostname)) {
      url.protocol = "https:";
      url.hostname = CANONICAL_HOST;
      url.port = "";
      return withSecurityHeaders(Response.redirect(url.toString(), 301));
    }

    return withSecurityHeaders(await env.ASSETS.fetch(request));
  },
};

function withSecurityHeaders(response) {
  const headers = new Headers(response.headers);
  for (const [name, value] of Object.entries(SECURITY_HEADERS)) {
    headers.set(name, value);
  }
  return new Response(response.body, {
    status: response.status,
    statusText: response.statusText,
    headers,
  });
}
