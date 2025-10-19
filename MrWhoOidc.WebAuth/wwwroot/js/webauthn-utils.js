/**
 * WebAuthn utility functions for MrWhoOidc
 * Provides base64 encoding/decoding and common WebAuthn operations
 */
class WebAuthnUtils {
    /**
     * Convert base64url string to ArrayBuffer
     */
    static base64ToArrayBuffer(base64) {
        // Handle base64url format (URL-safe base64)
        const binaryString = atob(base64.replace(/-/g, '+').replace(/_/g, '/'));
        const bytes = new Uint8Array(binaryString.length);
        for (let i = 0; i < binaryString.length; i++) {
            bytes[i] = binaryString.charCodeAt(i);
        }
        return bytes.buffer;
    }

    /**
     * Convert ArrayBuffer to base64url string
     */
    static arrayBufferToBase64(buffer) {
        const bytes = new Uint8Array(buffer);
        let binaryString = '';
        for (let i = 0; i < bytes.byteLength; i++) {
            binaryString += String.fromCharCode(bytes[i]);
        }
        // Convert to base64url format (URL-safe base64)
        return btoa(binaryString)
            .replace(/\+/g, '-')
            .replace(/\//g, '_')
            .replace(/=/g, '');
    }

    /**
     * Check if WebAuthn is supported by the browser
     */
    static isSupported() {
        return !!(navigator.credentials && 
                 navigator.credentials.create && 
                 navigator.credentials.get &&
                 window.PublicKeyCredential);
    }

    /**
     * Check if platform authenticator is available (Windows Hello, Touch ID, etc.)
     */
    static async isPlatformAuthenticatorAvailable() {
        if (!this.isSupported()) {
            return false;
        }

        try {
            return await PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable();
        } catch {
            return false;
        }
    }

    /**
     * Prepare credential creation options by converting base64 strings to ArrayBuffers
     */
    static prepareCredentialCreationOptions(options) {
        return {
            ...options,
            challenge: this.base64ToArrayBuffer(options.challenge),
            user: {
                ...options.user,
                id: this.base64ToArrayBuffer(options.user.id)
            },
            excludeCredentials: options.excludeCredentials?.map(cred => ({
                ...cred,
                id: this.base64ToArrayBuffer(cred.id)
            }))
        };
    }

    /**
     * Prepare assertion options by converting base64 strings to ArrayBuffers
     */
    static prepareAssertionOptions(options) {
        return {
            ...options,
            challenge: this.base64ToArrayBuffer(options.challenge),
            allowCredentials: options.allowCredentials?.map(cred => ({
                ...cred,
                id: this.base64ToArrayBuffer(cred.id)
            }))
        };
    }

    /**
     * Format credential response for server submission
     */
    static formatCredentialResponse(credential) {
        return {
            id: credential.id,
            rawId: this.arrayBufferToBase64(credential.rawId),
            response: {
                clientDataJSON: this.arrayBufferToBase64(credential.response.clientDataJSON),
                attestationObject: this.arrayBufferToBase64(credential.response.attestationObject)
            },
            type: credential.type
        };
    }

    /**
     * Format assertion response for server submission
     */
    static formatAssertionResponse(assertion) {
        return {
            id: assertion.id,
            rawId: this.arrayBufferToBase64(assertion.rawId),
            response: {
                clientDataJSON: this.arrayBufferToBase64(assertion.response.clientDataJSON),
                authenticatorData: this.arrayBufferToBase64(assertion.response.authenticatorData),
                signature: this.arrayBufferToBase64(assertion.response.signature),
                userHandle: assertion.response.userHandle ? 
                    this.arrayBufferToBase64(assertion.response.userHandle) : null
            },
            type: assertion.type
        };
    }

    /**
     * Get friendly error message for WebAuthn errors
     */
    static getErrorMessage(error) {
        if (!error) return 'An unknown error occurred.';

        switch (error.name) {
            case 'NotAllowedError':
                return 'Operation was cancelled or timed out. Please try again.';
            case 'NotSupportedError':
                return 'WebAuthn is not supported by your browser or device.';
            case 'InvalidStateError':
                return 'No compatible authenticators found. Please check your security key or device settings.';
            case 'ConstraintError':
                return 'The authenticator does not support the requested operation.';
            case 'NotReadableError':
                return 'The authenticator could not complete the operation. Please try again.';
            case 'UnknownError':
                return 'An unknown error occurred with the authenticator.';
            case 'SecurityError':
                return 'The operation was blocked for security reasons.';
            default:
                return error.message || 'An unexpected error occurred.';
        }
    }

    /**
     * Get CSRF token from the page (if available)
     */
    static getCSRFToken() {
        const token = document.querySelector('input[name="__RequestVerificationToken"]');
        return token ? token.value : null;
    }

    /**
     * Create headers for API requests including CSRF token
     */
    static createHeaders() {
        const headers = {
            'Content-Type': 'application/json'
        };

        const csrfToken = this.getCSRFToken();
        if (csrfToken) {
            headers['RequestVerificationToken'] = csrfToken;
        }

        return headers;
    }
}

// Make available globally
window.WebAuthnUtils = WebAuthnUtils;