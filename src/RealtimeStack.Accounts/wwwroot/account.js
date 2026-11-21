const byId = id => document.getElementById(id);
let session;
async function request(path, method = 'GET') {
  const response = await fetch(path, { method, credentials: 'same-origin', cache: 'no-store', headers: method === 'POST' ? { 'X-RealtimeStack-CSRF': session?.csrfToken || '' } : {} });
  const body = response.status === 204 ? {} : await response.json();
  if (!response.ok) throw new Error(body.error || 'Your account is temporarily unavailable. Please retry.');
  return body;
}
async function refresh() {
  session = await request('/api/auth/sso/session');
  byId('login').hidden = !session.configured || session.authenticated;
  byId('logout').hidden = !session.authenticated;
  byId('register').hidden = !session.authenticated || session.registrationAllowed === false;
  byId('account').hidden = !session.authenticated || !session.accountUrl;
  byId('admin-accounts').hidden = !session.authenticated || !session.adminAccountsUrl;
  if (session.accountUrl) byId('account').href = session.accountUrl;
  if (session.adminAccountsUrl) byId('admin-accounts').href = session.adminAccountsUrl;
  byId('identity').textContent = !session.configured ? 'Account registration has not been configured for this installation.' : session.authenticated ? `Signed in as ${session.user?.email || session.user?.name || 'your central account'}.` : 'Sign in to register this installation.';
  if (session.configured) {
    const login = new URL(session.loginUrl, location.href);
    if (login.origin !== location.origin) throw new Error('The sign-in address could not be verified.');
    byId('login').href = login.href;
  }
  if (session.authenticated) showReceipt(await request('/api/product/registration'));
}
function showReceipt(receipt) {
  const registered = receipt.status === 'registered';
  byId('status').textContent = registered ? `Registered installation ${receipt.installationId}. Free, with no expiration.` : 'This installation has not been registered to your account.';
  byId('register').textContent = registered ? 'Verify registration' : 'Register this installation';
}
function showError(error) { byId('status').textContent = error.message; }
byId('register').addEventListener('click', async () => { byId('register').disabled = true; try { await refresh(); showReceipt(await request('/api/product/registration', 'POST')); } catch (error) { showError(error); } finally { byId('register').disabled = false; } });
byId('logout').addEventListener('click', async () => { try { await request('/api/auth/sso/logout', 'POST'); byId('status').textContent = ''; await refresh(); } catch (error) { showError(error); } });
refresh().catch(showError);
