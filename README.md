# Locless Disk на C#

Откройте `LoclessDisk.slnx` в IDE или запустите из этой папки в Windows PowerShell:

```powershell
dotnet restore .\LoclessDisk.slnx
dotnet build .\LoclessDisk.slnx --no-restore
dotnet run --project .\LoclessDisk.Server -- --AdminPort=8081
```

Панель доступна на http://127.0.0.1:8081. Сохраните путь к существующей папке образов
в настройках, затем запустите сканирование в разделе «Образы».

Папка `EFI` содержит iPXE, shim и wimboot из Go-версии. Для стенда положите
локальный `boot.wim` в `EFI/WinPE`, а бинарник агента — в `EFI/agent`.
Эти локальные файлы не входят в Git. PXE выключен по умолчанию.
Настройки стенда приведены в [README сервера](LoclessDisk.Server/README.md).

Интеграционный тест `LoclessDisk.Images.Tests/restore-vhd.sh` восстанавливает образ
в новый VHD и проверяет GPT и каталог Windows. Для него нужны `sfdisk`, `qemu-img`
и `fls`; запускайте его в WSL Codex.
