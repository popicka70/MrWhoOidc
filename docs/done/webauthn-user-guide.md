# WebAuthn/FIDO2 User Guide

> **Last Updated:** October 19, 2025  
> **Feature:** Passwordless Authentication with Security Keys

## Overview

WebAuthn (FIDO2) allows users to authenticate using physical security keys, biometric authenticators (like Windows Hello or Touch ID), or platform authenticators instead of passwords. This provides stronger, phishing-resistant authentication.

---

## 📱 Supported Authenticators

### Hardware Security Keys
- **YubiKey** (USB-A, USB-C, NFC)
- **Google Titan Security Key**
- **Feitian ePass FIDO**
- **SoloKeys**
- Any FIDO2-compliant security key

### Platform Authenticators
- **Windows Hello** (fingerprint, facial recognition, PIN)
- **Touch ID** (macOS, iOS)
- **Face ID** (iOS, iPad)
- **Android Biometrics** (fingerprint, face unlock)

### Browser Requirements
- Chrome/Edge 67+
- Firefox 60+
- Safari 13+
- Opera 54+

---

## 🔧 Setting Up WebAuthn (First-Time Registration)

### Method 1: From Account Settings (Recommended)

1. **Sign in to your account** using your username and password
   - Navigate to the login page
   - Enter your credentials

2. **Navigate to Account Settings**
   - After login, go to your account dashboard
   - Look for **"Account Settings"** or click your profile

3. **Access WebAuthn Management**
   - In the account menu, select **"Security"** or **"WebAuthn Security Keys"**
   - You'll see a page listing your registered security keys (initially empty)

4. **Register Your First Security Key**
   - Click the **"Add Security Key"** button
   - A modal dialog will appear asking for a friendly name

5. **Name Your Security Key**
   - Enter a descriptive name (e.g., "YubiKey 5C", "Windows Hello", "Main Laptop")
   - This helps you identify the key later if you have multiple keys
   - Click **"Register"**

6. **Complete Browser Prompt**
   - Your browser will show a WebAuthn prompt
   - **For USB keys**: Insert your key and touch the button/sensor
   - **For Windows Hello**: Complete biometric or PIN authentication
   - **For Touch ID**: Touch the sensor on your device

7. **Confirmation**
   - You'll see a success message
   - The page will refresh showing your newly registered key
   - The key appears in your credentials list with:
     - Friendly name you provided
     - Registration date
     - Last used date (when applicable)

### Method 2: During MFA Enrollment

If your tenant administrator has enabled mandatory MFA:

1. **First Login After MFA Requirement**
   - After successful password authentication
   - You'll be redirected to MFA enrollment page

2. **Choose WebAuthn Option**
   - You may see options for TOTP and/or WebAuthn
   - Select **"Set up Security Key"**

3. **Follow Registration Steps**
   - Same as steps 5-7 above

---

## 🔐 Using WebAuthn to Sign In

### Passwordless Login Flow

Once you have a security key registered, you can use it to sign in:

1. **Navigate to Login Page**
   - Go to your authentication server login page

2. **Choose WebAuthn Authentication**
   - Below the password field, you'll see:
     ```
     ─────────── or ───────────
     [🔐 Sign in with Security Key]
     ```
   - Click the **"Sign in with Security Key"** button

3. **WebAuthn Authentication Page**
   - You'll be redirected to `/Auth/WebAuthn`
   - The page shows:
     - Optional username field (for usernameless flow support)
     - **"Authenticate with Security Key"** button
     - Instructions

4. **Optional: Enter Username**
   - **Usernameless flow**: Leave blank to use any registered key
   - **Username flow**: Enter your username if required by your organization

5. **Authenticate**
   - Click **"Authenticate with Security Key"**
   - Your browser will prompt you to use your authenticator
   - **For USB keys**: Insert key and touch button
   - **For Windows Hello**: Complete biometric/PIN
   - **For Touch ID**: Touch sensor

6. **Success**
   - After successful authentication, you'll be redirected to:
     - The page you were trying to access (if `returnUrl` was provided)
     - Your tenant home page
     - Root dashboard

### Authentication with MFA

If you have TOTP (Time-based One-Time Password) enabled:

1. **WebAuthn Authentication**
   - Complete steps 1-5 above

2. **TOTP Verification**
   - After security key authentication succeeds
   - You'll be redirected to TOTP verification page
   - Enter your 6-digit TOTP code from your authenticator app

3. **Complete Sign-In**
   - After TOTP verification, you're fully authenticated

---

## 🔧 Managing Your Security Keys

### Viewing Registered Keys

1. **Navigate to Account → WebAuthn**
   - Path: `/Account/WebAuthn`
   - Shows all your registered credentials

2. **Key Information Displayed**
   - **Friendly Name**: The name you gave the key
   - **Registered Date**: When you added the key
   - **Last Used**: Last successful authentication (if applicable)
   - **Credential ID**: Unique identifier (partially masked for security)

### Registering Additional Keys

**Best Practice: Register at least 2 keys for backup**

1. **From Account → WebAuthn page**
   - Click **"Add Security Key"** again
   - Follow the same registration process
   - Name each key uniquely (e.g., "Backup YubiKey", "Phone Biometric")

2. **Use Cases for Multiple Keys**
   - Primary key for daily use
   - Backup key stored securely at home
   - Platform authenticator on phone
   - Platform authenticator on laptop

### Removing a Security Key

> ⚠️ **Warning**: Ensure you have at least one other authentication method before removing a key!

1. **Navigate to Account → WebAuthn**
2. **Locate the Key to Remove**
   - Find the key in your credentials list
3. **Delete Action**
   - Click the delete/remove button (if implemented)
   - Confirm the deletion
4. **Verification**
   - The key will be removed from your account
   - You can no longer use it to authenticate

---

## 🚨 Troubleshooting

### "WebAuthn is not supported by your browser"

**Solution:**
- Update to the latest browser version
- Try a different modern browser (Chrome, Edge, Firefox, Safari)
- Ensure you're accessing via HTTPS (required for WebAuthn)

### "Authentication was cancelled or timed out"

**Causes:**
- User cancelled the browser prompt
- Timeout (usually 60 seconds)
- Security key not touched in time

**Solution:**
- Try again
- Ensure you touch the key when prompted
- If using USB key, try a different USB port

### "No registered security keys found"

**Causes:**
- Trying to use WebAuthn before registering a key
- Wrong username entered

**Solution:**
- Register a security key first via Account Settings
- Verify you're using the correct username
- Contact administrator if you believe keys should be registered

### "This security key is already registered"

**Cause:**
- Attempting to register the same physical key twice

**Solution:**
- The key is already registered and available for use
- Register a different key if you want a backup

### Windows Hello Not Appearing

**Solution:**
- Ensure Windows Hello is set up in Windows Settings
- Verify your browser has permission to use Windows Hello
- Try using an external security key instead

---

## 🔐 Security Best Practices

### Key Management

1. **Register Multiple Keys**
   - Always have at least 2 keys registered
   - Keep backup key in a secure location

2. **Use Unique Names**
   - Name keys descriptively
   - Helps identify which key to use
   - Easier to manage multiple keys

3. **Physical Security**
   - Store backup keys securely
   - Don't leave USB keys unattended
   - Report lost keys immediately

### When to Remove Keys

- Key is lost or stolen
- Replacing old key with new one
- Leaving organization (admin may remove)
- Device is retired or sold

### Account Recovery

If you lose access to all your security keys:

1. **Password Authentication**
   - You can still use username/password if enabled
   - Contact administrator if password-only is disabled

2. **Contact Administrator**
   - Tenant administrators can:
     - Reset your WebAuthn credentials
     - Assign temporary access
     - Help set up new keys

3. **Recovery Codes** (if implemented)
   - Some tenants may provide recovery codes
   - Store these securely offline
   - Use only in emergency

---

## 📋 Quick Reference

### Registration Checklist

- [ ] Sign in with username/password
- [ ] Navigate to Account → WebAuthn
- [ ] Click "Add Security Key"
- [ ] Enter friendly name
- [ ] Complete browser prompt
- [ ] Verify key appears in list
- [ ] Register backup key (recommended)

### Authentication Checklist

- [ ] Navigate to login page
- [ ] Click "Sign in with Security Key"
- [ ] (Optional) Enter username
- [ ] Click "Authenticate with Security Key"
- [ ] Complete browser prompt
- [ ] If MFA enabled: Enter TOTP code
- [ ] Redirected to destination

---

## 🆘 Support

### User Support

For help with WebAuthn:
- Contact your tenant administrator
- Check your organization's IT support portal
- Review this documentation

### Administrator Support

For administrator guides:
- See [WebAuthn Administration Guide](./webauthn-admin-guide.md) (if available)
- Check tenant settings for WebAuthn policies
- Review authentication logs for debugging

---

## 📚 Additional Resources

### Understanding WebAuthn/FIDO2

- **What is WebAuthn?**: A web standard for passwordless authentication
- **Why use it?**: Phishing-resistant, convenient, secure
- **How it works**: Public key cryptography with hardware-backed keys

### Privacy & Security

- **Private keys never leave device**: Keys stored in hardware/TPM
- **No shared secrets**: Each service gets unique credentials
- **Resistant to phishing**: Physical presence required
- **No tracking**: Cannot be used to track users across sites

### Compatibility

- **Cross-platform**: Use same key on Windows, Mac, Linux, mobile
- **Cross-browser**: Works across modern browsers
- **Offline capable**: Authentication doesn't require internet for key operation

---

**Document Version:** 1.0  
**Last Updated:** October 19, 2025  
**Related Documentation:**
- [WebAuthn Implementation Summary](./webauthn-implementation-summary.md) (if created)
- [Security Best Practices](./security-best-practices.md) (if exists)
- [MFA Configuration Guide](./mfa-configuration.md) (if exists)
