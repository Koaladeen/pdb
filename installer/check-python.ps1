# Checks that the bundled Python is the original, signed by the Python Software Foundation.
param([Parameter(Mandatory = $true)][string]$Dir)
foreach ($f in "python.exe", "pythonw.exe", "python312.dll") {
    $s = Get-AuthenticodeSignature (Join-Path $Dir $f)
    if ($s.Status -ne "Valid" -or $s.SignerCertificate.Subject -notmatch "Python Software Foundation") {
        Write-Error "$f signature: $($s.Status)"
        exit 1
    }
}
Write-Host "   Python signature OK"
