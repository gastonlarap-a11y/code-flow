# Propuesta de rediseño — CodeFlow 2.0: navegación nueva + identidad visual viva

> **Propuesta aprobada (2026-08-07), lista para implementar por fases (§10).** Basada en exploración completa del código y en investigación de patrones (GitKraken, Tower, VS Code + extensiones Azure Boards/Atlassian, JetBrains, Linear, Raycast, Arc).
>
> Directrices del usuario: (1) la arquitectura de navegación y los menús deben cambiar tanto que **la app no se parezca a la actual**; (2) CI reducido a cero consumo de Actions (§9). Empezar por la Fase 0, que es independiente del rediseño.

## 1. Contexto

CodeFlow (Electron + sidecar .NET 10 + React 19 + Tailwind 4 + Zustand) tiene hoy: TitleBar → TabBar horizontal (Graph/Changes/Editor | API) → Sidebar izquierdo (workspace + proyectos + git + PRs) → AiPanel derecho dock → TerminalDock inferior → StatusBar. Ya existe un sistema de diseño interno sólido (`docs/UX-REDESIGN.md`: tokens, primitivas en `components/common/`, CI guard `ui-conventions.test.mjs`, 21 temas + 8 acentos con tests de contraste).

Objetivos de esta iteración:
1. **Arquitectura de navegación y menús completamente nueva** — la app debe verse y sentirse otra.
2. **Colores más vivos** manteniendo profesionalismo y accesibilidad.
3. **Preparada a futuro** para implementar nuevos modulos dentro de la app a futuro

Lo que **sí se conserva** (es infraestructura, no apariencia): tokens/escala tipográfica como mecanismo, primitivas base (`Button`, `IconButton`, `Modal`, `Tabs`…), CI guard, i18n, accesibilidad, los 21 temas como capa de pintura. Todo lo demás — disposición, chrome, menús, jerarquía — cambia.

## 2. Diagnóstico (resumen)

- Navegación no escalable: `uiStore.activeView` es un union cerrado de 4 vistas; el TabBar no absorbe módulos nuevos.
- Tres barras horizontales apiladas (TitleBar + TabBar + StatusBar) comen altura y reparten la misma información en tres sitios.
- Sidebar sobrecargado (workspace + proyectos + git + PRs) y asimétrico respecto al AiPanel.
- 7 entradas distintas a Settings; 3 pickers separados (comandos/archivos/branch); 4 idiomas de sub-navegación.
- Paleta correcta pero apagada; deuda de contraste documentada (`--cf-danger` dark en 4 temas; acento-como-texto en 6/8 acentos claros).
- Sin abstracción de tickets: `lib/vcsProviders.ts` = `github | azure` y nada más.
- Duplicación: `FileTree` vs `CollectionTree` (árboles virtualizados paralelos).

## 3. Concepto: de "IDE con pestañas" a "workbench command-first"

La identidad nueva se apoya en cuatro decisiones que juntas hacen que la app no se parezca en nada a la actual:

1. **Una sola barra superior de comando** (muere el trío TitleBar + TabBar + StatusBar).
2. **Sidebar de navegación expandible con etiquetas** (estilo Linear/Slack), no un cajón de contenido mixto.
3. **Vista Home/Hub nueva** como aterrizaje: la primera pantalla ya no es un grafo de commits.
4. **Todo el contenido vive en "islas"** (cards flotantes sobre fondo ambiental) — desaparece el chrome flush pegado a los bordes; un solo lenguaje visual en vez de los dos actuales.

## 4. Arquitectura de navegación propuesta

### 4.1 Command Header — una única barra superior (~48px)

Sustituye TitleBar + TabBar + StatusBar. Tres zonas:

```
┌────────────────────────────────────────────────────────────────────────┐
│ ◀ ▶  ⦿ code-flow ▾ · ⎇ main ↑2↓0     [ ⌘K Buscar o ejecutar… ]  ⟳ ✦ ⌨ ⚙ │
└────────────────────────────────────────────────────────────────────────┘
  izquierda: historial +           centro: command bar    derecha: acciones
  contexto (proyecto pill,         siempre visible        globales (sync,
  branch pill con ahead/behind)                           AI, terminal,
                                                          settings/avatar)
```

- **Izquierda — contexto vivo**: back/forward (el `navigationStore` actual), **pill de proyecto** con el color de acento del workspace (clic → switcher), **pill de branch** con ahead/behind integrado (clic → picker de branches). La información que hoy reparte la StatusBar sube aquí; la StatusBar desaparece.
- **Centro — command bar siempre visible** (patrón Linear/Arc/Raycast): un input real, no solo un atajo. Unifica los tres pickers actuales (`CommandPalette`, `FilePalette`, `BranchSwitcherModal`, que ya comparten `PickerModal`) en un solo campo con **scopes por prefijo**: `>` comandos, `@` archivos, `⎇` branches, y a futuro `#` work items. Un solo lugar para "ir a cualquier sitio / hacer cualquier cosa".
- **Derecha — acciones globales**: fetch/sync (con countdown como anillo de progreso en el icono), toggle AiPanel, toggle terminal, y **un único punto de entrada a Settings** (se eliminan las 7 entradas dispersas; se conserva `Mod+,` y el scope `>` como atajos).
- Window controls nativos integrados (spacer mac / caption buttons Windows, como hoy en TitleBar).

### 4.2 Navigation Sidebar — expandible, con etiquetas y badges

El sidebar deja de ser un cajón de contenido y pasa a ser **navegación pura** (estilo Linear/Slack): lista vertical de módulos con icono + etiqueta + badge, colapsable a rail de iconos (48px) con un toggle.

```
┌──────────────┐
│ ⌂ Home       │        Módulos (registry §4.4):
│──────────────│
│ REPO         │        repo-scoped:
│ ⎇ Historial ⑫│          Home · Historial (Graph) · Cambios · Editor
│ ± Cambios  ❸ │        workspace-scoped:
│ ⌨ Editor     │          API · Work Items (futuro)
│──────────────│
│ WORKSPACE    │        Badges: cambios sin commit, PRs abiertas,
│ ⚡ API        │        work items asignados (a futuro)
│ ◫ Work Items │
│──────────────│
│ ⚙ Ajustes    │
└──────────────┘
```

- Grupos con **encabezados de scope visibles** ("Repo" / "Workspace") — la distinción que hoy es un icono `ScopeMarker` críptico pasa a ser texto.
- El contenido que hoy vive en el Sidebar (proyectos, branches, stashes, PRs) **se muda al Context Panel del módulo** (§4.3): navegación y contenido dejan de competir por el mismo espacio.
- Selección con `ActivePill` existente; colapsado, los labels se vuelven tooltips (`IconButton` ya cumple el guard).

### 4.3 Context Panel por módulo + DetailDrawer

Segunda columna, contextual al módulo activo (se puede ocultar):
- **Home**: sin panel (la vista es full-width).
- **Historial/Cambios/Editor**: proyectos del workspace, branches/stashes/remotes, PRs — lo que hoy satura el Sidebar, ahora ordenado por módulo.
- **API**: el `CollectionTree` (hoy incrustado en `ApiView`) se muda aquí — mismo patrón que el resto.
- **Work Items (futuro)**: lista agrupada por estado/sprint, filtros "míos / mencionado / siguiendo" (agrupamiento de la extensión oficial de Azure Boards).

**`DetailDrawer` (primitiva nueva)**: panel deslizante desde la derecha para detalle de entidad (work item, PR, stash) — el patrón dominante en GitKraken/GitHub/Atlassian. Los modales quedan solo para confirmaciones y acciones cortas.

### 4.4 Registry de módulos (habilitador técnico)

Sustituir el union `activeView` por un registro declarativo (nuevo `renderer/src/lib/modules.ts`):

```ts
type AppModule = {
  id: string;                    // "home" | "graph" | ... | "workitems"
  icon: LucideIcon;              // vía lib/ui/icons.ts
  labelKey: TranslationKey;      // i18n en/es
  scope: "repo" | "workspace";
  view: LazyExoticComponent;
  contextPanel?: LazyExoticComponent;
  badge?: (state) => number | null;
  commandScope?: string;         // prefijo en la command bar ("#" work items)
};
```

`uiStore.activeView` pasa a id validado contra el registry; `navigationStore` no cambia de forma. **Agregar work items = 1 entrada en el registry.**

### 4.5 Vista Home/Hub (nueva)

Aterrizaje por defecto al abrir la app (hoy aterriza en el grafo de commits). Grid de cards:
- **Proyectos recientes** (abrir con un clic, estado de branch/cambios en la card).
- **PRs abiertas** que esperan revisión (deep-link al PR review del AiPanel).
- **Actividad reciente de IA** (últimos análisis/chats, con checkpoint).
- **Accesos rápidos** (clonar, abrir carpeta, nueva request API).
- Hueco natural para **"Mis work items"** cuando exista el módulo.

Es la pieza que más cambia la primera impresión y no existe hoy nada parecido.

### 4.6 Paneles como "islas" — un solo lenguaje de chrome

Hoy conviven dos lenguajes documentados: vistas con `CARD` (radio 12px, borde, sombra) sobre fondo ambiental, y docks flush (Sidebar, AiPanel, Terminal) pegados al borde sin radio. Propuesta: **unificar todo en islas** — AiPanel y Terminal pasan a paneles con radio-card, sombra y margen, flotando sobre el fondo ambiental igual que las vistas. Con los gradientes ambientales potenciados (§5), el efecto conjunto es inmediatamente reconocible como "otra app".

## 5. Sistema de color: "vivo pero profesional"

La investigación (Linear, Raycast, VS Code) converge: base desaturada + **un** acento saturado protagonista + cuarteto semántico fijo. Más viveza ≠ más colores.

- **Migrar la paleta a OKLCH**: subir chroma de los 8 acentos manteniendo lightness → colores más vivos con contraste predecible. `accentStore.test.ts` sigue siendo el árbitro (4.5:1).
- **Superficies con tinte de acento**: `color-mix(in oklch, var(--cf-surface), var(--cf-accent) 3–4%)` en superficies raised → toda la app "respira" el acento elegido sin romper los 21 temas.
- **Fondo ambiental protagonista**: los gradientes `--cf-ambient-1/2/3` y del header pasan de casi imperceptibles a perceptibles-pero-tranquilos; son el lienzo de las islas (§4.6).
- **Nuevo token `--cf-info`** (azul) completando success/warning/danger/info.
- **Saldar la deuda documentada**: corregir `--cf-danger` dark en los 4 temas en falla; regla para acento-como-texto: solo en tamaños ≥ `--text-relaxed` o sobre `--cf-surface-raised`; en zonas densas, acento solo como fondo/borde/indicador.
- **Colores de proveedor = bucket de excepción**, solo chips de identidad: Azure azul (~#0078D4), Jira azul/púrpura Atlassian, GitHub neutro. Los colores por tipo de issue de Jira/Azure son configurables por proyecto → tratarlos como default, verificar contra instancias reales al implementar. Estados de work item con la paleta semántica propia (`--cf-info` en progreso, `--cf-success` done), no con la del proveedor.

## 6. Componentes: nuevos y promociones

**Nuevas primitivas en `components/common/`:**
- `Chip`/`Badge` — estado (semántico), proveedor (excepción), contador (sidebar/header). Variantes `solid|soft|outline`.
- `CommandBar` — el input central del header (evolución de `PickerModal` con scopes por prefijo).
- `DetailDrawer` — §4.3.
- `HubCard` — card de la vista Home (título, métrica/preview, acción primaria).
- `VirtualizedTree` — unifica `FileTree` + `CollectionTree` (+ futura lista de work items) sobre `@tanstack/react-virtual` con flatten genérico y drag por puntero.

**Promociones a `common/`:** `LabeledField` y `RevealToggle` (hoy en `api/`).

**Sin cambios de API:** `Button`, `IconButton`, `Modal`, `Tabs`, `Tooltip`, `Select`, `Toast`, `Skeleton`, `EmptyState` — reciben solo el refresh de tokens.

## 7. Cimientos para el módulo de work items (futuro)

Esta iteración deja listo, sin implementar la feature:
1. Registry de módulos (§4.4) con `workitems` como entrada futura y scope `#` en la command bar.
2. **Registry de proveedores con capacidades**: `lib/vcsProviders.ts` evoluciona de union `github|azure` a `{ id, vcs, prs, workItems }` — Jira (solo work items) convive con GitHub/ADO (todo).
3. **Settings → "Integraciones" unificada**: una fila por proveedor con Connect (OAuth/PAT), patrón GitKraken; `ConnectGithubModal`/`ConnectAdoModal` son la base.
4. Primitivas `Chip` y `DetailDrawer` disponibles.

Diseño funcional futuro (validado por investigación): lista en Context Panel → detalle en `DetailDrawer` → acciones: **crear branch desde item** (nombre auto con la clave), **transicionar estado**, **abrir en navegador**, **vincular PR ↔ item**. **Smart references** en el mensaje de commit (`AB#123`, `PROJ-45`, `#123`) con autocomplete — el gap más pedido en las herramientas estudiadas, y CodeFlow ya tiene el campo de commit con IA donde encaja. Flujo objetivo: *elegir item → start work (branch) → cambios → commit con referencia → PR vinculada → review IA → merge → transición de estado*.

## 8. Validaciones realizadas (puntos que estaban al 80%)

Verificado directamente en el código durante la planificación:

1. **Window controls en el Command Header — resuelto, riesgo bajo.** En Windows la ventana es totalmente frameless (`frame: false` en `shell/src/main.ts:106`) y los botones minimizar/maximizar/cerrar ya son componentes React en `TitleBar.tsx` que llaman `win.minimize()`/etc. vía el bridge: portarlos al header nuevo es mover un componente. En macOS (`titleBarStyle: "hidden"`, traffic lights en x:20/y:22) el header solo debe reservar ese spacer izquierdo, como hoy.
2. **Mudanza de `CollectionTree` al Context Panel — resuelto, riesgo bajo.** El componente no recibe estado por props desde `ApiView`: lee todo de stores zustand (`apiTreeStore`, `apiTabsStore`, `apiDragStore`, `apiModalStore`). Cambiarlo de columna no toca el estado de requests/sockets, que vive en stores y sobrevive porque las vistas quedan montadas-ocultas.
3. **Hex de Jira/Azure — deja de ser factor de riesgo de diseño.** La decisión es: estados con la paleta semántica propia, color de proveedor solo en chips de identidad. El hex exacto es un lookup en el momento de implementar (además son configurables por proyecto en ambos productos).

## 9. CI/CD: mínimo consumo de Actions (decisión del usuario)

**Evidencia medida** (repo privado `gastonlarap-a11y/code-flow`, runners `windows-2025` que facturan minutos ×2):
- `release.yml` histórico: **7.5–14 min por run** con tests incluidos. Recortado (sin tests) se estimó primero en 5–7 min, pero **la estimación era errónea**: medido paso a paso sobre un run real da **≈4m30s** (ver punto 3 del plan), así que sí cumple el límite de 5 min y el workflow se conserva.
- `ci-web.yml` y `ci-sidecar.yml` corren en **cada PR y push a main** (tests, lint, audits, typecheck, build con 6 GB de heap) — el gasto recurrente real.
- Hallazgo del historial: el 2026-08-06 hubo **3 runs de release el mismo día**, incluido uno **cancelado tras 6 h 22 min** (16:11→22:33) — probable causa principal de la cuota casi llena.
- Aclaración: el `.dmg` de Mac **nunca usó Actions** (se compila local vía `scripts/publish-release.sh`); no existe "doble release" por el .dmg — los runs extra fueron re-ejecuciones del workflow de Windows.

**Plan ejecutado** (revisado 2026-08-07 con medición real; los puntos 3–5 originales quedaron obsoletos y se anotan abajo):

1. **Comentar íntegros `ci-web.yml` y `ci-sidecar.yml`**: contenido completo comentado (no borrado), con nota de cabecera explicando por qué y cómo reactivarlos. Son el gasto recurrente real — **49 de los últimos 60 runs**, frente a 7 de `release`.
2. **Gates en local**: las validaciones que hacía el CI ya las corre `scripts/release.sh` antes de publicar (`dotnet build` + `dotnet test`, `pnpm -C renderer typecheck|lint|test`, `pnpm -C shell test|lint`, audits). No hacía falta script nuevo para esto.
3. **`release.yml` se conserva, recortado** — corrige la estimación de arriba. Desglose medido del último run exitoso (7m27s): setup 56s · `dotnet test` **2m36s** · test shell 12s · test renderer 25s · **build del instalador 2m59s** · hash+upload 14s. Quitando los tres pasos de test quedan **≈4m30s**, por debajo del límite de 5 min (los `pnpm install` no se pierden: `build-app.sh` los repite). Se le añade además la caché de NuGet que solo tenía `ci-sidecar.yml`, y el `upload-artifact` pasa a correr solo cuando el run no publica nada. **El `.exe` de Windows se conserva.**
4. **~~Nuevo `scripts/release-mac.sh`~~ — innecesario.** `release.sh` ya hace bump → gates → `publish-release.sh` (que construye el `.dmg`, tag y subida) → espera el `.exe` → verifica los 4 artefactos. En su lugar se añade **`scripts/build-dmg.sh`**: construye el `.dmg` y su `.sha256` en local, sin git ni GitHub, para tener el instalador sin cortar una release. `publish-release.sh` lo reutiliza.
5. **~~Adaptar `scripts/release.sh`~~ — no hacía falta.** El gate de CI (`release.sh:101-132`) pregunta por los *check-runs del commit*, no por los workflows: sin CI activo la respuesta es vacía y el script sigue por su rama documentada ("no CI ran for this commit"). La espera del instalador Windows sigue siendo válida porque `release.yml` sigue vivo.
6. **Consecuencia asumida**: se pierden los 4 tests exclusivos de Windows (`CredentialStoreTests` y compañía) — `ci-sidecar.yml` era su único hogar y ahora no corren en ningún sitio. Tocar el credential store o el transporte IPC es motivo para descomentar ese workflow y ejecutarlo a mano antes de publicar.

## 10. Fases de implementación (cuando se decida ejecutar)

| Fase | Contenido | Riesgo |
|---|---|---|
| 0 | CI/CD: comentar `ci-web.yml` + `ci-sidecar.yml`, recortar `release.yml`, `scripts/build-dmg.sh` (§9) — independiente del rediseño, se hace primero | Bajo |
| 1 | Tokens: OKLCH, tinte de superficies, `--cf-info`, ambient/gradientes protagonistas, fixes de contraste, propagación a `codeThemes.ts` | Bajo — CSS/tokens, tests vigilan |
| 2 | Registry de módulos + Command Header (retiro de TitleBar/TabBar/StatusBar) + `CommandBar` unificando los 3 pickers | Alto — toca `App.tsx`, `uiStore`, `navigationStore`, atajos |
| 3 | Navigation Sidebar + Context Panels (mudanza de proyectos/git/PRs y de `CollectionTree`) + islas (AiPanel/Terminal con chrome card) | Medio-alto |
| 4 | Vista Home/Hub + `HubCard` + `Chip` + `DetailDrawer` + `VirtualizedTree` unificado | Medio |
| 5 | Settings "Integraciones" + registry de proveedores con capacidades | Bajo-medio |
| — | (Futuro, fuera de alcance) Módulo work items: IPC nuevo en sidecar .NET, stores, UI | — |

Cada fase deja la app funcional y entregable por separado.

### Estado de ejecución (rama `feat/codeflow-2.0-redesign`)

Las seis fases ejecutadas, un commit por fase. Correcciones al plan aplicadas sobre la marcha, con
su motivo:

- **§4.6, islas.** No se añadió una constante hermana de `CARD`: unificar los dos lenguajes en uno
  es precisamente lo contrario a tener dos constantes con el mismo string. `CARD` es ahora el chrome
  de todo panel, y `.cf-ambient-bg` subió al contenedor de la zona de trabajo para ser el lienzo
  compartido. El padding exterior pasó de cada vista a la fila de la app.
- **§4.3, Context Panel.** Se mudó `ApiSidebar` **entero**, no sólo su `CollectionTree`: el árbol,
  su strip de pestañas y su buscador son una unidad, y partirla dejaba a `ApiView` sin contenido.
  El rail del Editor y la lista de `ChangesPanel` **no** se movieron — son contenido de la vista, no
  contexto del repositorio, y §4.3 no los pedía.
- **§4.2, Ajustes en la sidebar.** No se añadió: el Command Header ya se declara la única puerta
  siempre visible a Ajustes y dos puertas al mismo overlay no ayudan a nadie.
- **§4.5, "proyectos recientes".** No existía dato de recencia — `projects` sólo tiene `sort_order`
  y `created_at`, y el store guardaba un único `last_active_project_id`. Se resolvió con una lista
  MRU en un `app_setting` (`recent_project_ids`), sin migración ni cambios en el sidecar. Es
  recencia real y persistente, pero local a la máquina.
- **§6, `VirtualizedTree`.** Se unificó lo que era **idéntico** — el `useVirtualizer`, el sizer, el
  posicionamiento absoluto de filas, las clases del contenedor, el fantasma de arrastre y la
  indentación. **No** se unificaron el flatten, el arrastre ni el markup de fila, y no por falta de
  tiempo: el árbol de ficheros mueve *dentro de* un directorio y el de colecciones *entre* hermanos
  ordenados, con zonas de borde y auto-expansión; una fila de fichero es un `<button>` y una de
  colección no puede serlo porque contiene sus propios botones; y sólo el de ficheros modela "aún
  no cargado". Fundirlos habría cambiado comportamiento y accesibilidad, no sólo código.
- **`Mod+B`** conserva su chord y su id, y pasa a ocultar el Context Panel en vez de toda la
  columna izquierda. **`Mod+0`** es Home: numerar de nuevo los `Mod+1..4` existentes habría sido
  peor trato que una tecla poco común.

### Pendiente, y por qué

- **Renombrar un espacio de trabajo no es posible en ninguna parte.** No falta la UI: no existe el
  comando en el sidecar. Se crean, se borran y se recolorean; el nombre queda como se escribió.
- **El `release.yml` recortado nunca se ha cronometrado** contra el límite de 5 min de la §9, ni se
  ha comprobado que la app detecta la actualización (`update_download` + `.sha256`). Requiere una
  release con tag.
- **El arrastre de los dos árboles no se ha probado a mano** tras extraerles `VirtualizedTree`.
  macOS rechaza los clics sintéticos hacia la app, así que lo único que ese refactor podía romper
  sigue necesitando una mano en el ratón.
- **§7.3, Integraciones.** La sección "Git hosting" era un strip de pestañas sobre dos formularios y
  seguía archivada con el id de sección `azure` — un nombre que dejó de ser cierto cuando se le
  añadió GitHub. Ahora es una fila por proveedor con su estado y sus capacidades, que despliega el
  formulario existente **sin tocarlo**; el id pasa a `integrations` y con él los tres deep-links que
  lo nombraban. Las pestañas eran además la forma equivocada: dicen "elige uno", y la pregunta real
  es "cuáles tienes configurados", que es una lista con estado en cada fila.
- **§7.1, `workitems` en el registry. Hecho, pero no se muestra.** El registry gana la entrada y el
  campo `comingSoon`, así que los `Record<ModuleId, …>` de `App.tsx` y `ContextPanel.tsx` ya tienen
  su hueco reservado y publicar el módulo será una entrada, no una búsqueda por cinco ficheros. Lo
  que **no** hace es aparecer en la navegación, ni en la paleta, ni en el ciclado: una fila muerta
  permanente no promete lo que viene, parece un control roto — y en español "Elementos de trabajo"
  más una etiqueta "Próximamente" ni siquiera cabe en el ancho de la barra.
- **§4.1, anillo del countdown. Hecho.** El total sale de `autoFetchSeconds` en `preferencesStore`,
  no de `fetchTimerStore`, que sólo sabe lo que queda; la geometría vive en `lib/ui/progressRing.ts`
  con test, porque los casos que se ven mal — intervalo cero, y el segundo posterior a acortarlo,
  cuando lo restante supera al total — son los que un SVG no puede afirmar en una suite sin DOM.
- **§4.2, badge de PRs abiertas. Hecho, en Inicio.** Es la vista que tiene la tarjeta de pull
  requests, así que el número y la lista que cuenta están a un clic. No se pide nada para pintarlo:
  usa lo que ya cargó el panel de contexto, y si no hay dato no hay badge.
- **§6, variantes de `Chip`. Hechas, con un aviso.** `outline` nace con su consumidor real (la
  etiqueta de override de identidad git, que era el único sitio del código con ese tratamiento);
  `solid` **no tiene ninguno** y queda como `DetailDrawer`: construido porque la propuesta lo nombra.
- **§6, promoción de `LabeledField` y `RevealToggle` a `common/`. No hecha, a propósito.** Sus siete
  consumidores están todos dentro de `api/`, y la sección de Integraciones no los necesita: en
  Settings un input va envuelto en `settings/Field.tsx`, que es la regla. Mover dos ficheros a
  `common/` sin un solo consumidor fuera de su carpeta es ruido en el diff y una invitación a
  usarlos donde no tocan. Se promocionan el día que algo fuera de `api/` los pida.

## 11. Restricciones que el rediseño respeta (no negociables)

- CI guard de UI intacto (`ui-conventions.test.mjs`): sin `text-[Npx]`, `IconButton` con label, hit targets ≥24px.
- Paridad i18n en/es (test existente); atajos actuales se conservan aunque cambien los menús.
- Los 21 temas y 8 acentos siguen funcionando: todo color entra por tokens y `codeThemes.ts`.
- `prefers-reduced-motion`, focus rings, contraste 4.5:1.
- Reglas de `.claude/rules/renderer*.md`: sin `window.codeflow` en componentes, un store por dominio, Monaco solo vía `lib/monacoEditor.ts`, popover API para overlays.
- Las vistas siguen montadas-ocultas al cambiar de módulo (estado de terminal/sockets API se preserva).

## 12. Verificación (al implementar)

- `renderer`: lint + typecheck + suite Vitest (incluye `ui-conventions.test.mjs`, `accentStore.test.ts`).
- Skill `verify` del proyecto: lanzar CodeFlow real y validar header, sidebar, Home, islas y temas dark/light (muestreo de los 21 code themes) con capturas.
- Revisión manual de contraste en los 4 temas oscuros hoy en falla.
- Fase 0 (CI): `scripts/build-dmg.sh` produce el `.dmg` y su `.sha256`; el primer `release.yml` recortado se cronometra contra el límite de 5 min, y se verifica que la app detecta/descarga la actualización (`update_download` + `.sha256`).

---

**Confianza: 92%** — window controls, mudanza de `CollectionTree` y duraciones reales del CI verificados en código e historial de Actions. Queda sin verificar: la duración exacta de un `release.yml` recortado (estimada 5–7 min sobre datos históricos, nunca ejecutada — por eso se aplica la regla de comentar todo) y los hex por tipo de issue de Jira/Azure (lookup al implementar, sin impacto en el diseño).
