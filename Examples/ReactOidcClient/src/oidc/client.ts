import * as oauth from 'oauth4webapi'
import { OIDC } from './config'

let as: oauth.AuthorizationServer
let client: oauth.Client

export async function getAsAndClient() {
  if (!as) {
    const issuer = new URL(OIDC.authority)
    const discovery = await oauth.discoveryRequest(issuer)
    as = await oauth.processDiscoveryResponse(issuer, discovery)
  }
  if (!client) {
    client = {
      client_id: OIDC.clientId,
      token_endpoint_auth_method: 'none',
    }
  }
  return { as, client }
}

export async function startLogin() {
  const { as, client } = await getAsAndClient()

  // Pushed Authorization Request (PAR) with PKCE + state + nonce
  const codeVerifier = oauth.generateRandomCodeVerifier()
  const codeChallenge = await oauth.calculatePKCECodeChallenge(codeVerifier)
  const state = oauth.generateRandomState()
  const nonce = oauth.generateRandomNonce()

  const par = await oauth.pushedAuthorizationRequest(as, client, new URLSearchParams({
    client_id: client.client_id,
    response_type: 'code',
    redirect_uri: OIDC.redirectUri,
    scope: OIDC.scope,
    code_challenge: codeChallenge,
    code_challenge_method: 'S256',
    state,
    nonce,
  }))
  const parResponse = await oauth.processPushedAuthorizationResponse(as, client, par)

  const authUrl = new URL(as.authorization_endpoint!)
  authUrl.searchParams.set('client_id', client.client_id)
  authUrl.searchParams.set('request_uri', parResponse.request_uri)

  sessionStorage.setItem('pkce_code_verifier', codeVerifier)
  sessionStorage.setItem('oidc_state', state)
  sessionStorage.setItem('oidc_nonce', nonce)

  location.assign(authUrl)
}

export async function handleRedirectCallback(search: string) {
  const { as, client } = await getAsAndClient()
  const params = new URLSearchParams(search)

  const codeVerifier = sessionStorage.getItem('pkce_code_verifier')
  const expectedState = sessionStorage.getItem('oidc_state') ?? undefined
  const expectedNonce = sessionStorage.getItem('oidc_nonce') ?? undefined
  if (!codeVerifier) throw new Error('Missing PKCE verifier')

  const authRes = oauth.validateAuthResponse(as, client, params, expectedState)
  if (oauth.isOAuth2Error(authRes)) {
    throw new Error(`${authRes.error}: ${authRes.error_description}`)
  }

  const result = await oauth.authorizationCodeGrantRequest(
    as,
    client,
    authRes,
    OIDC.redirectUri,
    codeVerifier
  )

  const response = await oauth.processAuthorizationCodeOpenIDResponse(as, client, result, expectedNonce)
  let claims = oauth.getValidatedIdTokenClaims(response)

  // Optionally call UserInfo if available and we have an access token
  if (response.access_token && as.userinfo_endpoint) {
    try {
      const userInfoRes = await oauth.userInfoRequest(as, client, response.access_token)
      const userInfo = await oauth.processUserInfoResponse(as, client, claims.sub, userInfoRes)
      claims = { ...claims, ...userInfo }
    } catch {
      // ignore userinfo errors for demo
    }
  }

  // Store tokens and claims
  const tokens = {
    id_token: response.id_token!,
    access_token: response.access_token,
    token_type: response.token_type,
    expires_in: response.expires_in,
  }
  sessionStorage.setItem('oidc_tokens', JSON.stringify(tokens))
  sessionStorage.setItem('oidc_claims', JSON.stringify(claims))

  // Clear transient state
  sessionStorage.removeItem('pkce_code_verifier')
  sessionStorage.removeItem('oidc_state')
  sessionStorage.removeItem('oidc_nonce')
}

export function getUser() {
  const claimsRaw = sessionStorage.getItem('oidc_claims')
  if (!claimsRaw) return null
  try { return JSON.parse(claimsRaw) } catch { return null }
}

export function getTokens() {
  const raw = sessionStorage.getItem('oidc_tokens')
  if (!raw) return null
  try { return JSON.parse(raw) } catch { return null }
}

export function logoutFrontend() {
  sessionStorage.removeItem('oidc_tokens')
  sessionStorage.removeItem('oidc_claims')
}

export async function startFrontChannelLogout() {
  const { as } = await getAsAndClient()
  const tokens = getTokens()
  const idTokenHint = tokens?.id_token

  logoutFrontend()

  if (as.end_session_endpoint && idTokenHint) {
    const url = new URL(as.end_session_endpoint)
    url.searchParams.set('id_token_hint', idTokenHint)
    url.searchParams.set('post_logout_redirect_uri', OIDC.postLogoutRedirectUri)
    location.assign(url)
    return
  }
  location.assign(OIDC.postLogoutRedirectUri)
}
