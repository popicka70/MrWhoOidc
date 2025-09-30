package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/coreos/go-oidc/v3/oidc"
	"github.com/lestrrat-go/jwx/v2/jwk"
	"github.com/lestrrat-go/jwx/v2/jwt"
)

const (
	defaultConfigPath = "config.json"
	configEnvVar      = "MRWHO_GO_API_CONFIG"
)

type config struct {
	ListenAddr        string   `json:"listen_addr"`
	Issuer            string   `json:"issuer"`
	Audience          string   `json:"audience"`
	JWKSRefresh       string   `json:"jwks_refresh"`
	TrustedActClients []string `json:"trusted_act_clients"`
}

type keyCache struct {
	mu          sync.RWMutex
	set         jwk.Set
	nextRefresh time.Time
}

type app struct {
	cfg               config
	logger            *slog.Logger
	provider          *oidc.Provider
	jwksURI           string
	actClientAllowSet map[string]struct{}
	refreshInterval   time.Duration
	httpClient        *http.Client
	keySetCache       keyCache
}

type meResponse struct {
	Subject        string         `json:"subject"`
	Audience       []string       `json:"audience"`
	Scopes         []string       `json:"scopes"`
	ExpiresAt      string         `json:"expires_at"`
	IssuedAt       string         `json:"issued_at"`
	ActorClientID  string         `json:"actor_client_id,omitempty"`
	ActorSubject   string         `json:"actor_subject,omitempty"`
	RawClaims      map[string]any `json:"raw_claims"`
	TokenValidFor  string         `json:"token_valid_for"`
	TokenRetrieved string         `json:"token_retrieved"`
}

func main() {
	logger := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo}))

	cfg, err := loadConfig()
	if err != nil {
		logger.Error("failed to load config", slog.Any("error", err))
		os.Exit(1)
	}
	if cfg.Issuer == "" || cfg.Audience == "" {
		logger.Error("issuer and audience are required")
		os.Exit(1)
	}

	ctx := context.Background()

	provider, err := oidc.NewProvider(ctx, cfg.Issuer)
	if err != nil {
		logger.Error("failed to create OIDC provider", slog.Any("error", err))
		os.Exit(1)
	}

	var discovery struct {
		JWKSURI string `json:"jwks_uri"`
	}
	if err := provider.Claims(&discovery); err != nil {
		logger.Error("failed to decode discovery document", slog.Any("error", err))
		os.Exit(1)
	}
	if discovery.JWKSURI == "" {
		logger.Error("discovery document missing jwks_uri")
		os.Exit(1)
	}

	refreshInterval := parseDurationWithDefault(cfg.JWKSRefresh, 2*time.Minute)

	httpClient := &http.Client{Timeout: 10 * time.Second}

	actSet := make(map[string]struct{}, len(cfg.TrustedActClients))
	for _, c := range cfg.TrustedActClients {
		c = strings.TrimSpace(c)
		if c != "" {
			actSet[c] = struct{}{}
		}
	}

	application := &app{
		cfg:               cfg,
		logger:            logger,
		provider:          provider,
		jwksURI:           discovery.JWKSURI,
		actClientAllowSet: actSet,
		refreshInterval:   refreshInterval,
		httpClient:        httpClient,
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/me", application.handleMe)
	mux.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("ok"))
	})

	logger.Info("starting Go sample API", slog.String("listen_addr", cfg.ListenAddr), slog.String("issuer", cfg.Issuer), slog.String("audience", cfg.Audience))
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

	if cfg.ListenAddr == "" {
		cfg.ListenAddr = ":5190"
	}

	return cfg, nil
}

func parseDurationWithDefault(raw string, fallback time.Duration) time.Duration {
	if strings.TrimSpace(raw) == "" {
		return fallback
	}
	if d, err := time.ParseDuration(raw); err == nil {
		return d
	}
	return fallback
}

func (a *app) handleMe(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	ctx, cancel := context.WithTimeout(r.Context(), 5*time.Second)
	defer cancel()

	token, rawToken, err := a.verifyBearer(ctx, r.Header.Get("Authorization"))
	if err != nil {
		if errors.Is(err, errUnauthorized) {
			http.Error(w, err.Error(), http.StatusUnauthorized)
			return
		}
		if errors.Is(err, errForbidden) {
			http.Error(w, err.Error(), http.StatusForbidden)
			return
		}
		a.logger.Error("token validation failed", slog.Any("error", err))
		http.Error(w, "token validation failed", http.StatusBadRequest)
		return
	}

	aud := token.Audience()
	subj := token.Subject()
	exp := token.Expiration()
	iat := token.IssuedAt()

	scopes := extractScopes(token)
	actorClient, actorSubject := extractActor(token)

	claims, err := token.AsMap(ctx)
	if err != nil {
		a.logger.Warn("failed to materialize claims", slog.Any("error", err))
		claims = map[string]any{"error": err.Error()}
	}

	expiresAt := "<not present>"
	validFor := "unknown"
	if !exp.IsZero() {
		expiresAt = exp.UTC().Format(time.RFC3339)
		validFor = time.Until(exp).Round(time.Second).String()
	}

	issuedAt := "<not present>"
	if !iat.IsZero() {
		issuedAt = iat.UTC().Format(time.RFC3339)
	}

	resp := meResponse{
		Subject:        subj,
		Audience:       aud,
		Scopes:         scopes,
		ExpiresAt:      expiresAt,
		IssuedAt:       issuedAt,
		ActorClientID:  actorClient,
		ActorSubject:   actorSubject,
		RawClaims:      claims,
		TokenValidFor:  validFor,
		TokenRetrieved: time.Now().UTC().Format(time.RFC3339),
	}

	if err := respondJSON(w, http.StatusOK, resp); err != nil {
		a.logger.Error("write response", slog.Any("error", err))
	} else {
		a.logger.Info("token accepted", slog.String("subject", subj), slog.String("aud", strings.Join(aud, ",")), slog.String("actor_client", actorClient), slog.Int("scope_count", len(scopes)))
		a.logger.Debug("raw token", slog.String("jwt", rawToken))
	}
}

var (
	errUnauthorized = errors.New("missing or invalid bearer token")
	errForbidden    = errors.New("actor client not permitted")
)

func (a *app) verifyBearer(ctx context.Context, header string) (jwt.Token, string, error) {
	if !strings.HasPrefix(strings.ToLower(header), "bearer ") {
		return nil, "", errUnauthorized
	}

	raw := strings.TrimSpace(header[7:])
	if raw == "" {
		return nil, "", errUnauthorized
	}

	set, err := a.getKeySet(ctx)
	if err != nil {
		return nil, "", err
	}

	token, err := jwt.ParseString(raw,
		jwt.WithKeySet(set),
		jwt.WithAudience(a.cfg.Audience),
		jwt.WithIssuer(a.cfg.Issuer),
		jwt.WithAcceptableSkew(30*time.Second),
		jwt.WithValidate(true),
	)
	if err != nil {
		return nil, "", fmt.Errorf("parse token: %w", err)
	}

	actorClient, _ := extractActor(token)
	if len(a.actClientAllowSet) > 0 {
		if actorClient == "" {
			return nil, "", errForbidden
		}
		if _, ok := a.actClientAllowSet[actorClient]; !ok {
			return nil, "", errForbidden
		}
	}

	return token, raw, nil
}

func (a *app) getKeySet(ctx context.Context) (jwk.Set, error) {
	now := time.Now()
	a.keySetCache.mu.RLock()
	if a.keySetCache.set != nil && now.Before(a.keySetCache.nextRefresh) {
		cached := a.keySetCache.set
		a.keySetCache.mu.RUnlock()
		return cached, nil
	}
	a.keySetCache.mu.RUnlock()

	opts := []jwk.FetchOption{}
	if a.httpClient != nil {
		opts = append(opts, jwk.WithHTTPClient(a.httpClient))
	}

	set, err := jwk.Fetch(ctx, a.jwksURI, opts...)
	if err != nil {
		return nil, fmt.Errorf("fetch jwks: %w", err)
	}

	a.keySetCache.mu.Lock()
	a.keySetCache.set = set
	if a.refreshInterval > 0 {
		a.keySetCache.nextRefresh = time.Now().Add(a.refreshInterval)
	} else {
		a.keySetCache.nextRefresh = time.Now().Add(2 * time.Minute)
	}
	a.keySetCache.mu.Unlock()

	return set, nil
}

func extractScopes(token jwt.Token) []string {
	scopeVal, ok := token.Get("scope")
	if !ok {
		return nil
	}
	switch v := scopeVal.(type) {
	case []any:
		scopes := make([]string, 0, len(v))
		for _, item := range v {
			if s, ok := item.(string); ok {
				scopes = append(scopes, s)
			}
		}
		return scopes
	case string:
		parts := strings.Fields(v)
		return parts
	default:
		return nil
	}
}

func extractActor(token jwt.Token) (clientID string, subject string) {
	act, ok := token.Get("act")
	if !ok {
		return "", ""
	}
	switch v := act.(type) {
	case map[string]any:
		if cid, ok := v["client_id"].(string); ok {
			clientID = cid
		}
		if sub, ok := v["sub"].(string); ok {
			subject = sub
		}
	case jwt.Token:
		if cid, ok := v.Get("client_id"); ok {
			if s, ok := cid.(string); ok {
				clientID = s
			}
		}
		if sub, ok := v.Get("sub"); ok {
			if s, ok := sub.(string); ok {
				subject = s
			}
		}
	}
	return clientID, subject
}

func respondJSON(w http.ResponseWriter, status int, payload any) error {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	encoder := json.NewEncoder(w)
	encoder.SetEscapeHTML(false)
	encoder.SetIndent("", "  ")
	return encoder.Encode(payload)
}
