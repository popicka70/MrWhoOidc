<?php
declare(strict_types=1);

const OIDC_UPSTREAMS = [
    'metadata' => 'https://mrwho.onrender.com/t/default/.well-known/openid-configuration',
    'token' => 'https://mrwho.onrender.com/t/default/token',
    'userinfo' => 'https://mrwho.onrender.com/t/default/userinfo',
];

function respondJson(int $statusCode, array $payload): never
{
    http_response_code($statusCode);
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store');
    echo json_encode($payload, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
    exit;
}

if (!function_exists('curl_init')) {
    respondJson(500, ['title' => 'cURL is not available on this host.']);
}

$kind = isset($_GET['kind']) ? (string)$_GET['kind'] : '';
$upstreamUrl = OIDC_UPSTREAMS[$kind] ?? null;

if ($upstreamUrl === null) {
    respondJson(400, ['title' => 'Unsupported proxy request.']);
}

$requestMethod = $_SERVER['REQUEST_METHOD'] ?? 'GET';
if ($kind === 'token' && $requestMethod !== 'POST') {
    respondJson(405, ['title' => 'Token proxy only supports POST.']);
}

if (($kind === 'metadata' || $kind === 'userinfo') && $requestMethod !== 'GET') {
    respondJson(405, ['title' => 'This proxy endpoint only supports GET.']);
}

$headers = ['Accept: application/json'];
$authorizationHeader = $_SERVER['HTTP_AUTHORIZATION'] ?? '';
$contentTypeHeader = $_SERVER['CONTENT_TYPE'] ?? '';

if ($authorizationHeader !== '') {
    $headers[] = 'Authorization: ' . $authorizationHeader;
}

if ($contentTypeHeader !== '') {
    $headers[] = 'Content-Type: ' . $contentTypeHeader;
}

$curlHandle = curl_init($upstreamUrl);
if ($curlHandle === false) {
    respondJson(500, ['title' => 'Failed to initialize upstream request.']);
}

$requestBody = file_get_contents('php://input');

curl_setopt_array($curlHandle, [
    CURLOPT_RETURNTRANSFER => true,
    CURLOPT_FOLLOWLOCATION => true,
    CURLOPT_TIMEOUT => 30,
    CURLOPT_CUSTOMREQUEST => $requestMethod,
    CURLOPT_HTTPHEADER => $headers,
    CURLOPT_POSTFIELDS => $requestMethod === 'POST' ? $requestBody : null,
]);

$responseBody = curl_exec($curlHandle);
if ($responseBody === false) {
    $errorMessage = curl_error($curlHandle);
    curl_close($curlHandle);
    respondJson(502, ['title' => 'Upstream OIDC request failed.', 'detail' => $errorMessage]);
}

$statusCode = (int)curl_getinfo($curlHandle, CURLINFO_RESPONSE_CODE);
$contentType = curl_getinfo($curlHandle, CURLINFO_CONTENT_TYPE) ?: 'application/json; charset=utf-8';
curl_close($curlHandle);

http_response_code($statusCode > 0 ? $statusCode : 502);
header('Content-Type: ' . $contentType);
header('Cache-Control: no-store');
echo $responseBody;