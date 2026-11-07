# Avisos de software de terceiros

O Velocity usa as ferramentas abaixo como programas externos. Elas não estão neste repositório. O comando `npm run tools` baixa os binários para `resources/tools/` e eles são incluídos nos instaladores gerados por `npm run dist`.

## aria2

- Projeto: https://github.com/aria2/aria2
- Licença: GPL-2.0-or-later
- Uso: torrents. Executado como processo separado, sem modificação.
- Quem distribuir um instalador do Velocity com o `aria2c.exe` embutido deve oferecer também o código-fonte da versão incluída, disponível no endereço acima.

## yt-dlp

- Projeto: https://github.com/yt-dlp/yt-dlp
- Licença: Unlicense
- Uso: download de vídeo. Executado como processo separado, sem modificação.

## Dependências do aplicativo

As bibliotecas npm e os pacotes NuGet seguem as licenças declaradas em cada pacote. O Electron é distribuído sob a licença MIT, e o texto dela acompanha o aplicativo empacotado em `LICENSE.electron.txt`.
