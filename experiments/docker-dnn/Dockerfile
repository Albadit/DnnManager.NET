# escape=`
# DNN Platform on IIS in a Windows container - the web half of the experiment (see README.md).
#
# The base image has IIS, ASP.NET 4.8.1 and ServiceMonitor.exe. Its host must be able to run ltsc2022 images: Windows
# Server 2022 (process isolation), or Windows 10/11 Pro/Enterprise and Windows Server 2025 with Hyper-V isolation.
FROM mcr.microsoft.com/dotnet/framework/aspnet:4.8.1-windowsservercore-ltsc2022

SHELL ["powershell", "-NoProfile", "-Command", "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue';"]

# DNN's own install package, from its GitHub release - unpacked once into the image, copied into the site's volume
# on the first start (scripts\web-entrypoint.ps1).
ARG DNN_VERSION=10.3.3
RUN $url = \"https://github.com/dnnsoftware/Dnn.Platform/releases/download/v$env:DNN_VERSION/DNN_Platform_$($env:DNN_VERSION)_Install.zip\"; `
    Invoke-WebRequest $url -OutFile C:\dnn.zip -UseBasicParsing; `
    Expand-Archive C:\dnn.zip C:\dnn-package; `
    Remove-Item C:\dnn.zip

# The Default Web Site serves C:\site (a volume); its app pool never idles out or recycles on its own, as a dev site.
# appcmd, not the WebAdministration module: the aspnet image removes IIS's management tools.
RUN New-Item -ItemType Directory C:\site | Out-Null; `
    & C:\Windows\System32\inetsrv\appcmd.exe set vdir 'Default Web Site/' /physicalPath:C:\site; `
    & C:\Windows\System32\inetsrv\appcmd.exe set apppool DefaultAppPool /processModel.idleTimeout:00:00:00 /recycling.periodicRestart.time:00:00:00; `
    if ($LASTEXITCODE -ne 0) { throw 'appcmd failed.' }

COPY scripts/web-entrypoint.ps1 scripts/sql.ps1 scripts/write-install-template.ps1 scripts/after-install.ps1 scripts/query.ps1 scripts/dnn-logs.ps1 C:/scripts/

ENV DNN_VERSION=${DNN_VERSION}
EXPOSE 80
ENTRYPOINT ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "C:\\scripts\\web-entrypoint.ps1"]
