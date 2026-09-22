# Chronicles Donation Bridge 1.6.2 Beta

DonationAlerts запускает игровые эффекты в моде «Хроники Припяти 4.26» на IX-Ray. Эта Beta-версия ориентирована на мод, совместимость с чистым IX-Ray не заявлена.

## Установка

1. Закройте игру и Bridge через **Выход** в трее.
2. Скачайте [`ChroniclesDonationBridge-1.6.2-beta-win-x64.zip`](https://github.com/hardcoon/chronicles-donation-bridge/releases/download/v1.6.2-beta/ChroniclesDonationBridge-1.6.2-beta-win-x64.zip) и распакуйте его в отдельную папку вне игры.
3. Скопируйте папку `ixr_addons` из архива в корень игры, где находятся `fsgame.ltx` и `bin`, с заменой файлов.
4. Запустите `ChroniclesDonationBridge.exe`, выберите корневую папку игры и подключите DonationAlerts в настройках.
5. Сначала проверяйте эффекты кнопкой **Тест** на отдельном сохранении, затем включайте обработку донатов.

При обновлении всегда заменяйте EXE и аддон вместе и полностью перезапускайте игру.

## Исходный код и лицензия

Проект распространяется как **source-available**, а не open source. Использование и изменение разрешены на условиях [MIT License с Commons Clause](LICENSE); продавать программу или основанный на ней сервис нельзя.

Сборка на Windows с .NET 8 SDK: `powershell -ExecutionPolicy Bypass -File .\build.ps1`. Проверки: `powershell -ExecutionPolicy Bypass -File .\test.ps1`.
