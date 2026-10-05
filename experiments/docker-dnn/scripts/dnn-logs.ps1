# Prints the end of every DNN log (Portals\_default\Logs) - for the tests on the host, which keep them with the results.
param([int]$Tail = 200)
foreach ($file in Get-ChildItem C:\site\Portals\_default\Logs -File -ErrorAction SilentlyContinue) {
    "=== $($file.Name)"
    Get-Content $file.FullName -Tail $Tail
}
