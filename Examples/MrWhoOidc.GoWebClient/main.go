package main

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"html/template"
	"io"
	"log/slog"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/coreos/go-oidc/v3/oidc"
	"github.com/google/uuid"
	"golang.org/x/oauth2"
)

const (
	defaultConfigPath  = "config.json"
	sessionCookieName  = "mrwho_go_session"
	stateEntropyBytes  = 32
	nonceEntropyBytes  = 32
	codeVerifierLength = 64
	templateNameHome   = "home"
	configEnvVar       = "MRWHO_GO_WEB_CONFIG"
)

type config struct {
	Issuer       string     `json:"issuer"`
	ClientID     string     `json:"client_id"`
	ClientSecret string     `json:"client_secret"`
	RedirectURL  string     `json:"redirect_url"`
	Scopes       []string   `json:"scopes"`
	UsePKCE      *bool      `json:"use_pkce"`
	ListenAddr   string     `json:"listen_addr"`
	APIBaseURL   string     `json:"api_base_url"`
	OBO          *oboConfig `json:"obo"`
}

type oboConfig struct {
	ClientID      string `json:"client_id"`
	ClientSecret  string `json:"client_secret"`
	Scope         string `json:"scope"`
	Audience      string `json:"audience"`
	CacheLifetime string `json:"cache_lifetime"`
}

type sessionData struct {
	mu              sync.RWMutex
	State           string
	Nonce           string
	CodeVerifier    string
	AccessToken     string
	RefreshToken    string
	TokenExpiry     time.Time
	RawIDToken      string
	IDTokenClaims   map[string]any
	UserInfo        map[string]any
	APIToken        string
	APITokenExpiry  time.Time
	APILastResponse string
	LastError       string
}

type sessionStore struct {
	mu       sync.RWMutex
	sessions map[string]*sessionData
}

type app struct {
	cfg             config
	provider        *oidc.Provider
	oauth2Config    oauth2.Config
	idTokenVerifier *oidc.IDTokenVerifier
	sessionStore    *sessionStore
	templates       *template.Template
	logger          *slog.Logger
	httpClient      *http.Client
	oboCacheTTL     time.Duration
	pkceEnabled     bool
}

type homeViewModel struct {
	LoggedIn       bool
	Issuer         string
	ClientID       string
	RedirectURL    string
	Scopes         []string
	AccessToken    string
	AccessExpiry   string
	RefreshToken   string
	RawIDToken     string
	IDTokenJSON    string
	UserInfoJSON   string
	APILastJSON    string
	Error          string
	APIBaseURL     string
	OBOEnabled     bool
	LastUpdatedUTC string
}

func main() {
	logger := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo}))

	cfg, err := loadConfig()
	if err != nil {
		logger.Error("failed to load config", slog.Any("error", err))
		os.Exit(1)
	}

	if cfg.Issuer == "" || cfg.ClientID == "" {
		logger.Error("issuer and client_id are required")
		os.Exit(1)
	}

	ctx := context.Background()

	provider, err := oidc.NewProvider(ctx, cfg.Issuer)
	if err != nil {
		logger.Error("failed to connect to issuer", slog.Any("error", err))
		os.Exit(1)
	}

	verifierConfig := &oidc.Config{ClientID: cfg.ClientID}
	idTokenVerifier := provider.Verifier(verifierConfig)

	oauth2Config := oauth2.Config{
		ClientID:     cfg.ClientID,
		ClientSecret: cfg.ClientSecret,
		Endpoint:     provider.Endpoint(),
		Scopes:       ensureDefaultScopes(cfg.Scopes),
		RedirectURL:  cfg.RedirectURL,
	}

	templates := template.Must(template.New(templateNameHome).Funcs(template.FuncMap{
		"prettyJSON": func(v any) string {
			if v == nil {
				return ""
			}
			bytes, err := json.MarshalIndent(v, "", "  ")
			if err != nil {
				return fmt.Sprintf("<error: %v>", err)
			}
			return string(bytes)
		},
	}).Parse(homeTemplate))

	ttl := 5 * time.Minute
	if cfg.OBO != nil && strings.TrimSpace(cfg.OBO.CacheLifetime) != "" {
		if parsed, err := time.ParseDuration(cfg.OBO.CacheLifetime); err == nil {
			ttl = parsed
		}
	}

	pkceEnabled := true
	if cfg.UsePKCE != nil {
		pkceEnabled = *cfg.UsePKCE
	} else if cfg.ClientSecret != "" {
		pkceEnabled = false
	}

	application := &app{
		cfg:             cfg,
		provider:        provider,
		oauth2Config:    oauth2Config,
		idTokenVerifier: idTokenVerifier,
		sessionStore:    &sessionStore{sessions: make(map[string]*sessionData)},
		templates:       templates,
		logger:          logger,
		httpClient:      &http.Client{Timeout: 15 * time.Second},
		oboCacheTTL:     ttl,
		pkceEnabled:     pkceEnabled,
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/", application.wrapSession(application.handleHome))
	mux.HandleFunc("/login", application.wrapSession(application.handleLogin))
	mux.HandleFunc("/callback", application.wrapSession(application.handleCallback))
	mux.HandleFunc("/logout", application.wrapSession(application.handleLogout))
	mux.HandleFunc("/call-api", application.wrapSession(application.handleCallAPI))
	mux.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("ok"))
	})

	logger.Info("starting Go web client", slog.String("listen_addr", cfg.ListenAddr), slog.String("issuer", cfg.Issuer))
	if err := http.ListenAndServe(cfg.ListenAddr, mux); err != nil {
		logger.Error("server exited", slog.Any("error", err))
		os.Exit(1)
	}
}

func loadConfig() (config, error) {
	path := os.Getenv(configEnvVar)
	if strings.TrimSpace(path) == "" {
		path = defaultConfigPath
	}

	abs, err := filepath.Abs(path)
	if err != nil {
		return config{}, err
	}

	raw, err := os.ReadFile(abs)
	if err != nil {
		return config{}, err
	}

	var cfg config
	if err := json.Unmarshal(raw, &cfg); err != nil {
		return config{}, err
	}

	if cfg.RedirectURL == "" {
		cfg.RedirectURL = "http://localhost:5080/callback"
	}
	if cfg.ListenAddr == "" {
		cfg.ListenAddr = ":5080"
	}
	if len(cfg.Scopes) == 0 {
		cfg.Scopes = []string{"openid", "profile", "offline_access"}
	}
	if !strings.Contains(strings.Join(cfg.Scopes, " "), "openid") {
		cfg.Scopes = append([]string{"openid"}, cfg.Scopes...)
	}

	return cfg, nil
}

func ensureDefaultScopes(scopes []string) []string {
	if len(scopes) == 0 {
		return []string{"openid", "profile"}
	}
	m := make(map[string]struct{}, len(scopes))
	for _, scope := range scopes {
		m[scope] = struct{}{}
	}
	if _, ok := m["openid"]; !ok {
		scopes = append([]string{"openid"}, scopes...)
	}
	return scopes
}

func (a *app) wrapSession(next func(http.ResponseWriter, *http.Request, string, *sessionData)) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		sessionID, session := a.getOrCreateSession(w, r)
		next(w, r, sessionID, session)
	}
}

func (a *app) getOrCreateSession(w http.ResponseWriter, r *http.Request) (string, *sessionData) {
	cookie, err := r.Cookie(sessionCookieName)
	if err != nil || cookie.Value == "" {
		sessionID := uuid.NewString()
		session := &sessionData{}
		a.sessionStore.set(sessionID, session)
		http.SetCookie(w, &http.Cookie{
			Name:     sessionCookieName,
			Value:    sessionID,
			Path:     "/",
			HttpOnly: true,
			SameSite: http.SameSiteLaxMode,
		})
		return sessionID, session
	}

	session := a.sessionStore.get(cookie.Value)
	if session == nil {
		session = &sessionData{}
		a.sessionStore.set(cookie.Value, session)
	}
	return cookie.Value, session
}

func (s *sessionStore) get(id string) *sessionData {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.sessions[id]
}

func (s *sessionStore) set(id string, data *sessionData) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.sessions[id] = data
}

func (s *sessionStore) delete(id string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.sessions, id)
}

func (a *app) handleHome(w http.ResponseWriter, r *http.Request, sessionID string, session *sessionData) {
	vm := homeViewModel{
		Issuer:      a.cfg.Issuer,
		ClientID:    a.cfg.ClientID,
		RedirectURL: a.cfg.RedirectURL,
		Scopes:      a.oauth2Config.Scopes,
		APIBaseURL:  a.cfg.APIBaseURL,
		OBOEnabled:  a.cfg.OBO != nil,
	}

	session.mu.RLock()
	if session.AccessToken != "" {
		vm.LoggedIn = true
		vm.AccessToken = session.AccessToken
		if !session.TokenExpiry.IsZero() {
			vm.AccessExpiry = session.TokenExpiry.UTC().Format(time.RFC3339)
		}
		vm.RefreshToken = session.RefreshToken
		vm.RawIDToken = session.RawIDToken
		vm.IDTokenJSON = prettyJSON(session.IDTokenClaims)
		vm.UserInfoJSON = prettyJSON(session.UserInfo)
		vm.APILastJSON = session.APILastResponse
		vm.Error = session.LastError
	} else {
		vm.Error = session.LastError
	}
	session.mu.RUnlock()

	vm.LastUpdatedUTC = time.Now().UTC().Format(time.RFC3339)

	if err := a.templates.ExecuteTemplate(w, templateNameHome, vm); err != nil {
		a.logger.Error("failed to render home", slog.Any("error", err))
		http.Error(w, "template error", http.StatusInternalServerError)
	}
}

func prettyJSON(v any) string {
	if v == nil {
		return ""
	}
	data, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return fmt.Sprintf("<error: %v>", err)
	}
	return string(data)
}

func (a *app) handleLogin(w http.ResponseWriter, r *http.Request, sessionID string, session *sessionData) {
	state, err := randomString(stateEntropyBytes)
	if err != nil {
		a.failSession(session, fmt.Errorf("generate state: %w", err))
		http.Redirect(w, r, "/", http.StatusFound)
		return
	}
	nonce, err := randomString(nonceEntropyBytes)
	if err != nil {
		a.failSession(session, fmt.Errorf("generate nonce: %w", err))
		http.Redirect(w, r, "/", http.StatusFound)
		return
	}

	var codeVerifier, codeChallenge string
	if a.pkceEnabled {
		codeVerifier, err = pkceCodeVerifier()
		if err != nil {
			a.failSession(session, fmt.Errorf("generate pkce: %w", err))
			http.Redirect(w, r, "/", http.StatusFound)
			return
		}
		codeChallenge = pkceCodeChallenge(codeVerifier)
	}

	session.mu.Lock()
	session.State = state
	session.Nonce = nonce
	session.CodeVerifier = codeVerifier
	session.LastError = ""
	session.mu.Unlock()

	opts := []oauth2.AuthCodeOption{oidc.Nonce(nonce)}
	if a.pkceEnabled {
		opts = append(opts,
			oauth2.SetAuthURLParam("code_challenge", codeChallenge),
			oauth2.SetAuthURLParam("code_challenge_method", "S256"),
		)
	}

	authURL := a.oauth2Config.AuthCodeURL(state, opts...)
	http.Redirect(w, r, authURL, http.StatusFound)
}

func (a *app) handleCallback(w http.ResponseWriter, r *http.Request, sessionID string, session *sessionData) {
	if errParam := r.URL.Query().Get("error"); errParam != "" {
		description := r.URL.Query().Get("error_description")
		a.failSession(session, fmt.Errorf("authorization server returned %s: %s", errParam, description))
		http.Redirect(w, r, "/", http.StatusFound)
		return
	}

	state := r.URL.Query().Get("state")
	code := r.URL.Query().Get("code")
	if code == "" {
		a.failSession(session, errors.New("missing authorization code"))
		http.Redirect(w, r, "/", http.StatusFound)
		return
	}

	session.mu.RLock()
	expectedState := session.State
	codeVerifier := session.CodeVerifier
	session.mu.RUnlock()

	if expectedState == "" || state != expectedState {
		a.failSession(session, errors.New("state mismatch"))
		http.Redirect(w, r, "/", http.StatusFound)
		return
	}

	token, err := a.exchangeCode(r.Context(), code, codeVerifier)
	if err != nil {
		a.failSession(session, fmt.Errorf("token exchange failed: %w", err))
		http.Redirect(w, r, "/", http.StatusFound)
		return
	}

	rawIDToken, ok := token.Extra("id_token").(string)
	if !ok {
		a.failSession(session, errors.New("token response missing id_token"))
		http.Redirect(w, r, "/", http.StatusFound)
		return
	}

	idToken, err := a.idTokenVerifier.Verify(r.Context(), rawIDToken)
	if err != nil {
		a.failSession(session, fmt.Errorf("id token validation failed: %w", err))
		http.Redirect(w, r, "/", http.StatusFound)
		return
	}

	session.mu.RLock()
	expectedNonce := session.Nonce
	session.mu.RUnlock()
	if expectedNonce != "" && idToken.Nonce != expectedNonce {
		a.failSession(session, errors.New("nonce mismatch"))
		http.Redirect(w, r, "/", http.StatusFound)
		return
	}

	idTokenClaims := map[string]any{}
	if err := idToken.Claims(&idTokenClaims); err != nil {
		a.failSession(session, fmt.Errorf("read id token claims: %w", err))
		http.Redirect(w, r, "/", http.StatusFound)
		return
	}

	userInfo, err := a.fetchUserInfo(r.Context(), token)
	if err != nil {
		a.logger.Warn("user info fetch failed", slog.Any("error", err))
	}

	session.mu.Lock()
	session.State = ""
	session.Nonce = ""
	session.CodeVerifier = ""
	session.AccessToken = token.AccessToken
	session.RefreshToken = token.RefreshToken
	session.TokenExpiry = token.Expiry
	session.RawIDToken = rawIDToken
	session.IDTokenClaims = idTokenClaims
	if userInfo != nil {
		session.UserInfo = userInfo
	}
	session.LastError = ""
	session.APIToken = ""
	session.APITokenExpiry = time.Time{}
	session.mu.Unlock()

	http.Redirect(w, r, "/", http.StatusFound)
}

func (a *app) handleLogout(w http.ResponseWriter, r *http.Request, sessionID string, session *sessionData) {
	a.sessionStore.set(sessionID, &sessionData{})
	a.logger.Info("session cleared", slog.String("session_id", sessionID))
	http.Redirect(w, r, "/", http.StatusFound)
}

func (a *app) handleCallAPI(w http.ResponseWriter, r *http.Request, sessionID string, session *sessionData) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	session.mu.RLock()
	accessToken := session.AccessToken
	session.mu.RUnlock()

	if accessToken == "" {
		a.failSession(session, errors.New("sign in before calling the API"))
		http.Redirect(w, r, "/", http.StatusFound)
		return
	}
	if strings.TrimSpace(a.cfg.APIBaseURL) == "" {
		a.failSession(session, errors.New("api_base_url is not configured"))
		http.Redirect(w, r, "/", http.StatusFound)
		return
	}

	tokenToUse := accessToken
	var expiry time.Time

	if a.cfg.OBO != nil {
		var err error
		tokenToUse, expiry, err = a.exchangeOnBehalfOf(r.Context(), session, accessToken)
		if err != nil {
			a.failSession(session, fmt.Errorf("on-behalf-of exchange failed: %w", err))
			http.Redirect(w, r, "/", http.StatusFound)
			return
		}
	}

	apiURL := strings.TrimRight(a.cfg.APIBaseURL, "/") + "/me"
	req, err := http.NewRequestWithContext(r.Context(), http.MethodGet, apiURL, nil)
	if err != nil {
		a.failSession(session, fmt.Errorf("create api request: %w", err))
		http.Redirect(w, r, "/", http.StatusFound)
		return
	}
	req.Header.Set("Authorization", "Bearer "+tokenToUse)

	resp, err := a.httpClient.Do(req)
	if err != nil {
		a.failSession(session, fmt.Errorf("call api: %w", err))
		http.Redirect(w, r, "/", http.StatusFound)
		return
	}
	defer resp.Body.Close()

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		a.failSession(session, fmt.Errorf("read api response: %w", err))
		http.Redirect(w, r, "/", http.StatusFound)
		return
	}

	session.mu.Lock()
	if resp.StatusCode >= 200 && resp.StatusCode < 300 {
		session.LastError = ""
	} else {
		session.LastError = fmt.Sprintf("API returned %d: %s", resp.StatusCode, strings.TrimSpace(string(body)))
	}
	session.APILastResponse = string(body)
	if !expiry.IsZero() {
		session.APIToken = tokenToUse
		session.APITokenExpiry = expiry
	}
	session.mu.Unlock()

	http.Redirect(w, r, "/", http.StatusFound)
}

func (a *app) exchangeOnBehalfOf(ctx context.Context, session *sessionData, subjectToken string) (string, time.Time, error) {
	session.mu.RLock()
	cachedToken := session.APIToken
	expiry := session.APITokenExpiry
	session.mu.RUnlock()

	if cachedToken != "" && time.Now().Add(10*time.Second).Before(expiry) {
		return cachedToken, expiry, nil
	}

	if a.cfg.OBO == nil {
		return subjectToken, time.Time{}, nil
	}

	form := url.Values{}
	form.Set("grant_type", "urn:ietf:params:oauth:grant-type:token-exchange")
	form.Set("subject_token", subjectToken)
	form.Set("subject_token_type", "urn:ietf:params:oauth:token-type:access_token")
	form.Set("requested_token_type", "urn:ietf:params:oauth:token-type:access_token")
	if strings.TrimSpace(a.cfg.OBO.Scope) != "" {
		form.Set("scope", a.cfg.OBO.Scope)
	}
	if strings.TrimSpace(a.cfg.OBO.Audience) != "" {
		form.Set("audience", a.cfg.OBO.Audience)
	}
	if strings.TrimSpace(a.cfg.OBO.ClientID) != "" && strings.TrimSpace(a.cfg.OBO.ClientSecret) == "" {
		form.Set("client_id", a.cfg.OBO.ClientID)
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, a.oauth2Config.Endpoint.TokenURL, strings.NewReader(form.Encode()))
	if err != nil {
		return "", time.Time{}, err
	}
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")

	if strings.TrimSpace(a.cfg.OBO.ClientSecret) != "" {
		req.SetBasicAuth(a.cfg.OBO.ClientID, a.cfg.OBO.ClientSecret)
	}

	resp, err := a.httpClient.Do(req)
	if err != nil {
		return "", time.Time{}, err
	}
	defer resp.Body.Close()

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return "", time.Time{}, err
	}

	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return "", time.Time{}, fmt.Errorf("token exchange failed (%d): %s", resp.StatusCode, strings.TrimSpace(string(body)))
	}

	var parsed struct {
		AccessToken string `json:"access_token"`
		TokenType   string `json:"token_type"`
		ExpiresIn   int    `json:"expires_in"`
		Scope       string `json:"scope"`
	}
	if err := json.Unmarshal(body, &parsed); err != nil {
		return "", time.Time{}, fmt.Errorf("parse token response: %w", err)
	}
	if strings.ToLower(parsed.TokenType) != "bearer" {
		return "", time.Time{}, fmt.Errorf("unexpected token type %q", parsed.TokenType)
	}
	if parsed.ExpiresIn > 0 {
		expiry = time.Now().Add(time.Duration(parsed.ExpiresIn) * time.Second)
	} else if a.oboCacheTTL > 0 {
		expiry = time.Now().Add(a.oboCacheTTL)
	}

	session.mu.Lock()
	session.APIToken = parsed.AccessToken
	session.APITokenExpiry = expiry
	session.mu.Unlock()

	return parsed.AccessToken, expiry, nil
}

func (a *app) exchangeCode(ctx context.Context, code, codeVerifier string) (*oauth2.Token, error) {
	opts := []oauth2.AuthCodeOption{}
	if a.pkceEnabled && codeVerifier != "" {
		opts = append(opts, oauth2.SetAuthURLParam("code_verifier", codeVerifier))
	}
	return a.oauth2Config.Exchange(ctx, code, opts...)
}

func (a *app) fetchUserInfo(ctx context.Context, token *oauth2.Token) (map[string]any, error) {
	if token == nil {
		return nil, nil
	}
	userInfo, err := a.provider.UserInfo(ctx, oauth2.StaticTokenSource(token))
	if err != nil {
		return nil, err
	}
	raw := map[string]any{}
	if err := userInfo.Claims(&raw); err != nil {
		return nil, err
	}
	return raw, nil
}

func (a *app) failSession(session *sessionData, err error) {
	session.mu.Lock()
	defer session.mu.Unlock()
	session.LastError = err.Error()
	a.logger.Error("session error", slog.Any("error", err))
}

func randomString(length int) (string, error) {
	bytes := make([]byte, length)
	if _, err := rand.Read(bytes); err != nil {
		return "", err
	}
	return base64.RawURLEncoding.EncodeToString(bytes), nil
}

func pkceCodeVerifier() (string, error) {
	allowed := "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~"
	bytes := make([]byte, codeVerifierLength)
	if _, err := rand.Read(bytes); err != nil {
		return "", err
	}
	for i := range bytes {
		bytes[i] = allowed[int(bytes[i])%len(allowed)]
	}
	return string(bytes), nil
}

func pkceCodeChallenge(verifier string) string {
	hash := sha256.Sum256([]byte(verifier))
	return base64.RawURLEncoding.EncodeToString(hash[:])
}

const homeTemplate = `{{define "home"}}
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <title>MrWhoOidc Go Web Client</title>
    <style>
        body { font-family: system-ui, sans-serif; max-width: 960px; margin: 2rem auto; padding: 0 1rem; }
        header { display: flex; justify-content: space-between; align-items: baseline; margin-bottom: 1.5rem; }
        nav a { margin-right: 1rem; }
        pre { background: #f4f4f4; padding: 1rem; border-radius: 6px; overflow-x: auto; }
        .card { border: 1px solid #ddd; border-radius: 6px; padding: 1rem; margin-bottom: 1.5rem; box-shadow: 0 1px 2px rgba(0,0,0,0.05); }
        .error { background: #ffecec; border: 1px solid #f5aca6; color: #b30000; padding: 0.75rem; border-radius: 6px; margin-bottom: 1rem; }
        .actions { margin-bottom: 1.5rem; }
        button, a.button { padding: 0.5rem 1rem; border-radius: 4px; border: none; background: #2563eb; color: white; text-decoration: none; cursor: pointer; }
        a.button.secondary { background: #475569; }
        button { font-size: 1rem; }
        section h2 { margin-top: 0; }
        footer { text-align: center; color: #666; margin-top: 3rem; font-size: 0.875rem; }
    </style>
</head>
<body>
<header>
    <div>
        <h1>MrWhoOidc Go Web Client</h1>
        <div><strong>Issuer:</strong> {{.Issuer}}</div>
        <div><strong>Client Id:</strong> {{.ClientID}}</div>
    </div>
    <nav>
        <a class="button" href="/login">Sign in</a>
        <a class="button secondary" href="/logout">Clear session</a>
    </nav>
</header>

{{if .Error}}
<div class="error">{{.Error}}</div>
{{end}}

<div class="card">
    <h2>Configuration</h2>
    <p><strong>Redirect URL:</strong> {{.RedirectURL}}</p>
    <p><strong>Requested scopes:</strong> {{range $index, $scope := .Scopes}}{{if $index}}, {{end}}{{$scope}}{{end}}</p>
    {{if .APIBaseURL}}<p><strong>API base URL:</strong> {{.APIBaseURL}} {{if .OBOEnabled}}(OBO enabled){{end}}</p>{{end}}
</div>

{{if .LoggedIn}}
<div class="card">
    <h2>Tokens</h2>
    <p><strong>Access token expires:</strong> {{.AccessExpiry}}</p>
    <pre>{{.AccessToken}}</pre>
    {{if .RefreshToken}}
    <p><strong>Refresh token</strong></p>
    <pre>{{.RefreshToken}}</pre>
    {{end}}
    <p><strong>ID token (raw)</strong></p>
    <pre>{{.RawIDToken}}</pre>
</div>

<div class="card">
    <h2>ID token claims</h2>
    <pre>{{.IDTokenJSON}}</pre>
</div>

{{if .UserInfoJSON}}
<div class="card">
    <h2>UserInfo response</h2>
    <pre>{{.UserInfoJSON}}</pre>
</div>
{{end}}

<div class="card">
    <h2>Call the sample API</h2>
    <form action="/call-api" method="post" style="display:inline;">
        <button type="submit">Invoke GET /me</button>
    </form>
    {{if .APILastJSON}}
    <h3>Last API response</h3>
    <pre>{{.APILastJSON}}</pre>
    {{end}}
</div>
{{else}}
<div class="card">
    <h2>Next steps</h2>
    <p>Use the Sign in button to start the authorization code + PKCE flow against your MrWhoOidc server.</p>
</div>
{{end}}

<footer>
    Last refreshed at {{.LastUpdatedUTC}} UTC
</footer>
</body>
</html>
{{end}}
`
