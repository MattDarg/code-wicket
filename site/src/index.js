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

    if (url.pathname.startsWith(PREVIEWS_PREFIX)) {
      return withSecurityHeaders(await servePreview(request, env, url));
    }

    return withSecurityHeaders(await env.ASSETS.fetch(request));
  },
};

// The preview feed Visual Studio polls, and the VSIX it names, published to R2 by preview.yml.
// Only those two shapes of key are served, so nothing else put in the bucket is reachable.
const PREVIEWS_PREFIX = "/previews/";
const PREVIEW_KEY = /^previews\/(feed\.atom|\d+\.\d+\.\d+\.\d+\/CodeWicket\.VSExtension\.vsix)$/;

async function servePreview(request, env, url) {
  if (request.method !== "GET" && request.method !== "HEAD") {
    return new Response("Method not allowed", { status: 405, headers: { Allow: "GET, HEAD" } });
  }

  const key = url.pathname.slice(1);
  if (!PREVIEW_KEY.test(key)) {
    return new Response("Not found", { status: 404 });
  }

  const object =
    request.method === "HEAD"
      ? await env.PREVIEWS.head(key)
      : await env.PREVIEWS.get(key, { onlyIf: request.headers });
  if (object === null) {
    return new Response("Not found", { status: 404 });
  }

  const headers = new Headers();
  object.writeHttpMetadata(headers);
  headers.set("ETag", object.httpEtag);
  headers.set("Content-Length", String(object.size));

  // A GET whose precondition failed comes back without a body.
  if (request.method === "GET" && !("body" in object)) {
    return new Response(null, { status: 304, headers });
  }
  return new Response(request.method === "HEAD" ? null : object.body, { headers });
}

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
