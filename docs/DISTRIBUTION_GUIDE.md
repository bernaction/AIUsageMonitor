# Guia de Distribuição e Automação de Release

Este documento detalha a estratégia de empacotamento e distribuição do **AI Usage Monitor**, inspirada no modelo do *LibreHardwareMonitor* (executável leve + `.dll`s na mesma pasta em formato *Self-Contained*), além de instruções para release automatizado via GitHub Actions.

---

## 1. Por que usar Self-Contained Multi-File?

Ao publicar aplicações .NET para Windows, existem dois caminhos principais:

| Formato | Descrição | Prós | Contras |
| :--- | :--- | :--- | :--- |
| **Single-File (`.exe` único)** | Tudo empacotado em um único executável. | Arquivo único para o usuário. | **Alto índice de falso-positivo em antivírus** (heurísticas confundem o *packer/dropper* com malware); inicialização mais lenta. |
| **Multi-File Self-Contained (Recomendado)** | `AIUsageMonitor.exe` + `.dll`s do .NET e dependências soltas na pasta compactada em `.zip`. | **Sem falsos-positivos de antivírus**; usuário **não precisa ter o .NET instalado**; inicialização ultra-rápida (*ReadyToRun*). | Distribuição em formato `.zip`. |

### Estrutura do Pacote Gerado (.zip)
```text
AIUsageMonitor/
├── AIUsageMonitor.exe               <- Carregador nativo do Windows (~150KB)
├── AIUsageMonitor.dll               <- Código principal compilado
├── AIUsageMonitor.runtimeconfig.json
├── Microsoft.Web.WebView2.Core.dll  <- Dependências
├── WebView2Loader.dll
├── System.*.dll                     <- Runtime do .NET 8 embutido
└── ...
```

---

## 2. Compilação Local Manual ou via Script

### Comando dotnet CLI
Para gerar a pasta de distribuição manualmente:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false -o ./dist/AIUsageMonitor
```

### Explicação dos parâmetros:
- `-c Release`: Compilação otimizada para produção.
- `-r win-x64`: Alvo para Windows 64-bit.
- `--self-contained true`: Inclui o runtime do .NET 8 (não exige instalação prévia pelo usuário).
- `-p:PublishSingleFile=false`: Mantém as DLLs separadas (estilo LibreHardwareMonitor).
- `-p:PublishReadyToRun=true`: Pré-compila para código de máquina nativo, reduzindo o tempo de abertura a milissegundos.
- `-p:DebugType=None -p:DebugSymbols=false`: Remove arquivos `.pdb` (símbolos de debug) para reduzir o tamanho.

### Usando o script PowerShell local (`build-release.ps1`)
Basta executar na raiz do projeto:
```powershell
./build-release.ps1
```
O script lê automaticamente a tag `<Version>` do `AIUsageMonitor.csproj` (ex: `0.1.2`), gerando a pasta `./dist/AIUsageMonitor` e o arquivo compactado versionado `./dist/AIUsageMonitor-v0.1.2-win-x64.zip`.

---

## 3. Automação de Release no GitHub Actions

O arquivo [`.github/workflows/release.yml`](../.github/workflows/release.yml) está configurado para automatizar todo o processo de build, empacotamento em `.zip` e publicação no GitHub Releases sempre que uma nova tag de versão for enviada.

### Fluxo de Criação de Release:

1. **Atualize a versão no arquivo `AIUsageMonitor.csproj`** (opcional, mas recomendado):
   ```xml
   <Version>0.1.3</Version>
   ```

2. **Faça o commit e crie a tag git**:
   ```powershell
   git add .
   git commit -m "chore: release v0.1.3"
   git tag v0.1.3
   ```

3. **Envie a tag para o GitHub**:
   ```powershell
   git push origin main
   git push origin v0.1.3
   ```

4. **O GitHub Actions fará tudo sozinho**:
   - Baixa o repositório.
   - Instala o .NET 8.
   - Compila o pacote *Self-Contained Multi-File*.
   - Compacta em `AIUsageMonitor-v0.1.3-win-x64.zip`.
   - Cria o **GitHub Release** correspondente à tag e anexa o `.zip` como asset de download.
