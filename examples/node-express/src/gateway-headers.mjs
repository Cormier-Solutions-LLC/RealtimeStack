export function gatewayHeaders(request, publicScheme) {
  for (const name of Object.keys(request.headers ?? {})) {
    if (name === "forwarded" || name.startsWith("x-forwarded-")) delete request.headers[name];
  }
  return { "x-forwarded-proto": publicScheme };
}
