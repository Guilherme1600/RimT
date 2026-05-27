# RimT – Multithreaded Performance Optimizer
### RimWorld 1.6 Mod

---

## O que faz este mod

Distribui os sistemas mais pesados do RimWorld por múltiplos núcleos de CPU:

| Sistema | O que faz |
|---|---|
| **Needs** | Calcula fome/descanso/alegria em threads de fundo |
| **Hauling cache** | Guarda em cache resultados "nada para carregar" 300 ticks |
| **Job throttle** | Animais pensam a cada 30 ticks, não cada tick |
| **Skills** | Não-colonos decaem skills a cada 8 ticks |
| **Apparel** | Debounce de 5 ticks em mudanças de equipamento |
| **Mental state** | Não-colonos verificados a cada 5 ticks |
| **Region warm** | Pré-aquece caches de pathfinding em background |
| **MapPawns** | Deduplica reconstrução de listas no mesmo tick |

---

## Instalação e compilação

### Pré-requisitos
- Visual Studio Code (já tens)
- [.NET SDK 4.7.2](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net472) OU [Build Tools for Visual Studio 2022](https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022)

### Passo a passo

1. **Abre o terminal no VSCode** (`Ctrl + ~`)

2. **Navega até à pasta do projeto:**
   ```
   cd "C:\Users\Guilherme Antonio\Desktop\RimT\Source\RimT"
   ```

3. **Compila:**
   ```
   dotnet build RimT.csproj -c Release /p:RIMWORLD_MANAGED="C:\Users\Guilherme Antonio\Documents\RimWorld.v1.6.4630\game\RimWorldWin64_Data\Managed"
   ```

4. **Ou corre o script de build:**
   ```
   .\build.bat
   ```

5. O ficheiro `RimT.dll` é gerado automaticamente em:
   ```
   C:\Users\Guilherme Antonio\Desktop\RimT\Assemblies\RimT.dll
   ```

6. **Copia a pasta `RimT`** para:
   ```
   C:\Users\Guilherme Antonio\Documents\RimWorld.v1.6.4630\game\Mods\RimT
   ```

7. Abre RimWorld → Mods → ativa **RimT - Multithreaded Performance**

---

## Overlay de performance

Pressiona **F10** em jogo para ver um overlay com:
- FPS atual
- TPS atual  
- Número de threads ativas

---

## Definições (Settings)

Vai a `Opções → Mods → RimT Performance`:

- **Worker threads** – quantos núcleos usar (padrão: CPUs - 2)
- **Offload needs** – needs em background
- **Offload health** – health checks em background
- **Offload relations** – relações em background
- **Offload hauling** – cache de hauling
- **Warm pathfinding** – pré-aquecer pathfinding
- **Debug log** – mensagens de debug no log

---

## Arquitetura de segurança

```
Main Thread                    Worker Threads
────────────────               ────────────────────────────
Tick() chamado          ──▶   Trabalho de leitura (snapshot)
                               |
                               ▼
                          MainThreadDispatcher.Post(result)
                               |
UpdatePlay() Postfix    ◀──   Queue de resultados
Flush() aplica estado
```

**Regra de ouro:** threads de fundo **NUNCA** escrevem no estado do jogo.  
Apenas lêem snapshots e colocam lambdas na queue do `MainThreadDispatcher`.  
Todos os writes acontecem na main thread durante o `Flush()`.

---

## Compatibilidade

- ✅ RimWorld 1.6
- ✅ Harmony 2.x (incluído no jogo)
- ✅ Rimefeller, Vanilla Expanded, etc. (não toca nos Defs deles)
- ⚠️ Mods que façam Harmony patches nos mesmos métodos podem conflituar

---

## Estrutura de ficheiros

```
RimT/
├── About/
│   └── About.xml
├── Assemblies/
│   └── RimT.dll          ← gerado pelo build
├── Defs/
│   └── GameComponents.xml
└── Source/
    └── RimT/
        ├── RimT.csproj
        ├── RimTMod.cs           ← entry point + settings
        ├── ThreadCoordinator.cs ← pool de threads
        ├── MainThreadDispatcher.cs ← fila de resultados
        ├── Patches.cs           ← todos os Harmony patches
        ├── PawnBatchScheduler.cs ← distribuição de ticks
        ├── PerformanceMonitor.cs ← overlay TPS/FPS
        └── build.bat
```
