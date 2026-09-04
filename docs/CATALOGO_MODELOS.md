# Catálogo de modelos: alta, precios, ciclo de vida y detección de cambios

Cómo se mantiene la lista de modelos que el usuario puede elegir, cómo se calcula
su coste, cómo se avisa de que un modelo va a dejar de funcionar y cómo se detecta que
ha aparecido uno nuevo o que ha cambiado una tarifa.

---

## 1. Dónde vive el catálogo

La lista está **duplicada a propósito** en dos sitios, y hay que tocar los dos:

| Fichero | Para qué | Qué guarda |
|---------|----------|------------|
| `Client/Pages/Modules.razor` → `AllModels` | Alta de módulos en la UI | id, nombre, tipos, capacidades, descripción, contexto |
| `Server/Services/Ai/ModelCatalog.cs` → `AllModels` | Todo lo que no pasa por la UI (bot de Telegram, ejecutor, endpoints) | id, nombre, proveedor, tipos, capacidades, contexto |

Los dos tienen que contener **exactamente los mismos ids**. Si solo añades uno, el
modelo aparece en la UI pero el bot de Telegram no lo ofrece y no sale en la pantalla
de modelos, o al revés. Ya pasó: el catálogo del servidor arrastraba diez modelos de
menos (embeddings, audio, transcripción y Canva) y nadie se enteró hasta que la
pantalla de precios los expuso. Hay un test que compara los dos ficheros
(`ModelPricingEndpointTests.ElCatalogoDelServidorTieneLosMismosModelosQueElDelCliente`).

Las **capacidades** y la **ventana de contexto** también tienen que coincidir: la
pantalla de modelos filtra por ellas y las cruza con el precio, y para eso tienen que
viajar por la API, no quedarse en el Razor. Otro test las compara capacidad a capacidad
(`LasCapacidadesDelServidorCoincidenConLasDelCliente`).

Alrededor hay tres tablas más que se sincronizan con estas:

- `Server/Services/Ai/PricingCatalog.cs` — tarifas.
- `Server/Services/Ai/ModelLifecycle.cs` — fechas de retirada.
- `Server/Services/Ai/VisionCapability.cs` — qué modelos aceptan imágenes.

---

## 2. Los modelos no se borran nunca

Cuando un proveedor retira un modelo, **no se quita del catálogo**: se marca en
`ModelLifecycle` y la UI avisa.

El motivo es que los módulos guardados apuntan a un id concreto. Si el id
desaparece de la lista, el desplegable se queda en blanco y la ejecución falla con
un error del proveedor que no explica nada. Dejándolo visible con la etiqueta
"Retirado" y el sustituto recomendado, el usuario ve qué pasó y a qué migrar.

---

## 3. Ciclo de vida: qué se puede automatizar y qué no

Hay dos preguntas distintas y solo una tiene respuesta automática.

### "¿Este modelo sigue existiendo?" → sí, automatizable

`ModelAvailabilityService` llama a `GET /v1/models` con la API key del tenant y
devuelve los ids que esa cuenta puede usar de verdad. Se cachea 6 h.

Detecta tres cosas que la tabla local no ve: modelos ya apagados, modelos nuevos
que aún no están en el catálogo, y modelos que existen pero a los que esa cuenta
concreta no tiene acceso.

Si la llamada falla, la disponibilidad viaja como `null` — que significa **"no lo
sé"**, no "no disponible". La UI solo avisa cuando el proveedor dice
explícitamente que no lo tiene.

### "¿Cuánto cuesta y cuándo lo apagan?" → no, es tabla local

**Ningún proveedor expone tarifas ni fechas de retirada por API.**

- `GET /v1/models` devuelve solo `id`, `created` y `owned_by`.
- Los endpoints de generación devuelven `usage` (tokens), nunca dólares.
- La *Costs API* de OpenAI (`/v1/organization/costs`) sí da gasto real, pero
  necesita una **admin key** distinta, viene agregada por día y con retardo de
  facturación: sirve para cuadrar la factura a fin de mes, no para estimar el
  coste de una ejecución concreta.

Por eso las tarifas y las fechas viven en tablas versionadas en el repo, con la
fecha de última revisión en el comentario de cabecera de cada fichero. Se revisan
a mano contra:

- <https://developers.openai.com/api/docs/pricing>
- <https://developers.openai.com/api/docs/deprecations>

---

## 4. Cómo se resuelve la tarifa de un modelo

`PricingCatalog.EstimateTextCost` busca en este orden:

1. **Match exacto** del id.
2. **El id sin sufijo de snapshot** — `gpt-5-mini-2025-08-07` → `gpt-5-mini`,
   `claude-opus-4-5-20251124` → `claude-opus-4-5`. Cada proveedor usa su formato y
   `ModelLifecycle.StripSnapshotSuffix` conoce los dos.
3. **La clave más larga que sea prefijo** del id, como último recurso.

El paso 3 tiene que ser **la más larga**, no la primera que encaje. Cuando cogía la
primera, cada modelo nuevo de una familia heredaba la tarifa del miembro más
antiguo del diccionario, y el error no daba ninguna señal: solo aparecía en la
factura. Ver `docs/fixes/COSTE_EJECUCION_PRICING.md`.

`PricingCatalog.HasExactTextPrice` dice si la tarifa es propia del modelo o
heredada de un pariente; la UI lo usa para marcar el coste como aproximado.

### Modelos de imagen

`gpt-image` factura la imagen como tokens de salida, y los tokens por
calidad+tamaño son fijos y publicados:

| Tamaño | low | medium | high |
|--------|-----|--------|------|
| 1024x1024 | 272 | 1056 | 4160 |
| 1024x1536 | 408 | 1584 | 6240 |
| 1536x1024 | 400 | 1568 | 6208 |

El coste por imagen es esos tokens por la tarifa de salida del modelo, que es lo
único que cambia entre versiones ($40/1M en `gpt-image-1`, $32/1M en
`gpt-image-1.5`, $30/1M en `gpt-image-2`). Cada versión tiene su tabla ya resuelta
en `PricingCatalog`.

### Embeddings, audio y transcripción

No comparten unidad de facturación, así que van en `AuxiliaryPrices` con la suya:
embeddings por millón de tokens, `tts-1`/`tts-1-hd` por millón de **caracteres**, y
las transcripciones por **minuto** de audio. Forzarlos todos a "$/1M tokens" sería
equivocarse por tres órdenes de magnitud en dos de los tres casos.

Canva no tiene entrada: se paga por suscripción, no por llamada. La UI lo muestra
como "sin coste por uso", que es distinto de un precio que falta.

---

## 5. Añadir un modelo nuevo

1. `Client/Pages/Modules.razor` → `AllModels`.
2. `Server/Services/Ai/ModelCatalog.cs` → `AllModels`, con **las mismas capacidades
   y el mismo contexto** que pusiste en el paso 1.
3. `Server/Services/Ai/PricingCatalog.cs` → su tarifa **propia** (no confiar en el
   match por prefijo).
4. Si el proveedor ya anunció su retirada, `ModelLifecycle` con fecha y sustituto.
5. Si acepta imágenes de entrada, `VisionCapability`.

El servicio de deteccion (§7) avisa de los modelos que hay que dar de alta: cuando el
proveedor lista un id que el catálogo no conoce, aparece en el histórico como
"Nuevo en el proveedor" con los tres ficheros que hay que tocar.

Los tests de `Server.Tests/CatalogoModelos/` fallan si te saltas el paso 3: hay una
comprobación de que **todo** modelo del catálogo tiene tarifa propia, y otra de que
todo modelo retirado indica un sustituto que sigue vivo.

---

## 6. Endpoints y pantalla de modelos

Los dos endpoints los sirve `ModelCatalogService`, que une catálogo, tarifas y ciclo
de vida y los resuelve contra las API keys del tenant.

`GET /api/models/lifecycle` devuelve, para cada modelo del catálogo del servidor:

```json
{
  "id": "gpt-image-1.5",
  "provider": "OpenAI",
  "status": "deprecated",          // active | deprecated | retired
  "shutdownDate": "2026-12-01",
  "daysUntilShutdown": 113,
  "replacementId": "gpt-image-2",
  "note": null,
  "available": true,               // null = no se ha podido comprobar
  "priceIsExact": null             // null en modelos que no son de texto
}
```

Lo consume `Client/Components/ModelLifecycleBadge.razor`, que pinta la etiqueta
compacta en las tablas y el aviso completo bajo el modelo seleccionado.

`GET /api/models/pricing` añade a lo anterior las tarifas de **todos** los modelos
—texto, imagen y los que se facturan por otra unidad—, sus capacidades y su ventana
de contexto. Alimenta la pantalla **Modelos** (`/modelos`, `Client/Pages/Models.razor`;
la ruta antigua `/precios` sigue apuntando ahí para no romper enlaces guardados).

El servidor manda las tarifas **por millón de tokens**, no el coste por ejecución: es
el cliente quien multiplica, para que el usuario pueda cambiar los tokens de entrada y
salida y ver la tabla recalcularse. Por defecto asume 10.000 de entrada y 2.000 de
salida —un artículo corto con su prompt de sistema— porque el precio por millón es
difícil de traducir a dinero real de un vistazo.

La barra de la tabla es **logarítmica**, y está etiquetada como tal: entre el modelo
más barato y el más caro hay tres órdenes de magnitud, y en escala lineal todo lo que
no fuese un modelo Pro sería una barra invisible. La escala se calcula sobre los
modelos visibles, así que al filtrar por proveedor las barras se reajustan a ese
conjunto.

Las filas se agrupan **por proveedor y, dentro, por familia**. Ordenar solo por precio
separaba a los hermanos: `gpt-image-1-mini` acababa seis filas por encima de
`gpt-image-2`, con Leonardo y DALL-E en medio, y parecía que faltaba del catálogo. La
familia sale del id (`ModelPriceResponse.Family`: los segmentos iniciales sin dígitos,
así que `gpt-image-1-mini` y `gpt-image-2` caen los dos en `gpt-image`), y las familias
se ordenan entre sí por su miembro más barato para no perder la lectura de precio.

La pantalla tiene tres pestañas:

- **Comparativa** — gráficas. Coste por ejecución, tarifa de entrada frente a la de
  salida, coste por imagen y ventana de contexto, más una nube de puntos que cruza
  precio y contexto para responder a "de los modelos que me valen, cuál sale más
  barato". Todas se recalculan con los filtros y con los tokens que ponga el usuario.
- **Tabla** — las tarifas fila a fila, con capacidades y contexto.
- **Cambios** — el servicio de detección y su histórico (§7).

Los filtros son tres y se combinan: **empresa** (proveedor), **tipo de generación**
(el `ModuleType`: texto, imagen, embeddings, audio, transcripción, diseño) y
**capacidades**, que se acumulan —marcar "visión" y "razonamiento" busca los que
tienen las dos—. Los componentes de gráfica viven en `Client/Components/Models/`.

Las gráficas se pintan con CSS (barras) y SVG inline (la nube): no hay ninguna
librería de charting, así que no hay nada que cargar de un CDN ni que actualizar.
`Client/Components/Models/SvgText.cs` existe porque Razor se reserva la etiqueta
`<text>` y no deja escribirla dentro de un bloque de código.

Cada gráfica compara **una sola unidad de facturación**. Los modelos de embeddings,
voz y transcripción se agrupan por unidad y cada grupo tiene la suya: mezclar dólares
por minuto con dólares por millón de caracteres en la misma escala compara cosas
distintas. Por el mismo motivo los modelos de imagen de precio plano van aparte de los
que cobran según la calidad.

---

## 7. Servicio de detección de cambios

`Server/Services/Ai/ModelCatalogScanService.cs`, lanzado a mano desde la pestaña
**Cambios** (`POST /api/models/scan`) y consultable en `GET /api/models/scan/history`.

Responde a dos preguntas distintas con dos fuentes distintas:

| Pregunta | Fuente | Qué apunta |
|----------|--------|------------|
| ¿Hay modelos nuevos? | El listado de modelos del proveedor | `provider_new_model`: el proveedor lo tiene y el catálogo no |
| ¿Han cambiado los precios? | La foto que dejó la pasada anterior | `price_change`, con el antes, el después y el porcentaje |

La segunda merece explicación: **ningún proveedor publica sus tarifas por API** (§3),
así que no hay nada contra lo que contrastar el precio de hoy salvo el precio que
había ayer. Por eso cada pasada guarda una foto del catálogo
(`ModelCatalogSnapshot`, una fila por modelo) y la siguiente compara contra ella.
Cuando alguien revisa `PricingCatalog.cs` y despliega, la pasada siguiente detecta qué
modelo cambió, de cuánto a cuánto y qué día. Sin la foto, esa información solo estaría
en el historial de git.

**"Actualizar" aquí es dejar la foto al día, no reescribir las tarifas.** El precio con
el que se factura sigue saliendo del código revisado a mano, que es lo único fiable
para cobrar.

Además de esos dos, apunta `lifecycle_change` (un modelo pasa a deprecated o retired),
`availability_change` (el proveedor deja de listar un modelo del catálogo, o vuelve a
listarlo) y `removed_model` (desaparece del catálogo del repo).

Tres decisiones que evitan que el histórico se llene de ruido:

- **La primera pasada de cada tenant solo fotografía** (`IsBaseline`). Si generase
  histórico, el día que se estrena la pantalla aparecerían los 72 modelos del catálogo
  como "nuevos" y el histórico nacería inservible.
- **Los snapshots con fecha no cuentan como modelo nuevo.** `gpt-5.6-sol-2026-03-11` es
  el mismo modelo que `gpt-5.6-sol`, y anunciarlo cada vez que OpenAI publica una
  instantánea sería un falso positivo semanal.
- **Cada hallazgo se anuncia una sola vez.** Un id que el proveedor lista y el catálogo
  no tiene se guarda como foto con `Source = "provider"`, así que sigue apareciendo en
  la lista de pendientes pero no se repite en cada pasada. El tope es de 40 hallazgos
  por pasada: OpenAI lista más de cien ids y volcarlos todos sería ruido.

`null` sigue significando **"no lo sé"**: si no se ha podido preguntar al proveedor, la
disponibilidad conocida no se pisa. Un corte de red no puede marcar medio catálogo como
retirado.

Las pasadas se guardan enteras (`ModelScanRun`), también las que no encuentran nada y
las que fallan: "se miró y no había cambios" es información, y sin ella no hay forma de
saber si el servicio se está lanzando. Las tres tablas son *tenant-scoped* y se crean
en `TenantDbContextFactory.ApplyPendingColumns`, como el resto del esquema.

### Listado de modelos por proveedor

`ModelAvailabilityService` pregunta a los cuatro proveedores que tienen endpoint de
listado; cada uno con su forma de autenticar, que es lo único que cambia:

| Proveedor | Endpoint | Key | Lista en |
|-----------|----------|-----|----------|
| OpenAI | `/v1/models` | `Authorization: Bearer` | `data[].id` |
| Anthropic | `/v1/models` | `x-api-key` + `anthropic-version` | `data[].id` |
| xAI | `/v1/models` | `Authorization: Bearer` | `data[].id` |
| Google | `/v1beta/models` | parámetro `key` | `models[].name`, con prefijo `models/` |

Leonardo y Canva no tienen listado: sus modelos solo salen del catálogo local. El
resultado se cachea 6 h, así que lanzar el escaneo dos veces seguidas no repite las
llamadas.
