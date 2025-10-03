export const OIDC = {
  authority: import.meta.env.VITE_OIDC_AUTHORITY ?? 'https://mrwho.onrender.com',
  clientId: import.meta.env.VITE_OIDC_CLIENT_ID ?? 'react-demo',
  redirectUri: (import.meta.env.VITE_REDIRECT_URI as string) ?? `${location.origin}/callback`,
  postLogoutRedirectUri: (import.meta.env.VITE_POST_LOGOUT_REDIRECT_URI as string) ?? `${location.origin}/`,
  scope: import.meta.env.VITE_OIDC_SCOPE ?? 'openid profile email',
} as const
