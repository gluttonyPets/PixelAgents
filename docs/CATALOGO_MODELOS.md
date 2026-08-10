# Catálogo de modelos: alta, precios y ciclo de vida

Cómo se mantiene la lista de modelos que el usuario puede elegir, cómo se calcula
su coste y cómo se avisa de que un modelo va a dejar de funcionar.

---

## 1. Dónde vive el catálogo

La lista está **duplicada a propósito** en dos sitios, y hay que tocar los dos:

| Fichero | Para qué | Qué guarda |
|---------|----------|------------|
| `Client/Pages/Modules.razor` → `AllModels` | Alta de módulos en la UI | id, nombre, tipos, capacidades, descripción, contexto |
| `Server/Services/Ai/ModelCatalog.cs` → `AllModels` | Todo lo que no pasa por la UI (bot de Telegram, ejecutor, endpoints) | id, nombre, proveedor, tipos |

El del servidor es un espejo reducido del cliente. Si solo añades uno de los dos,
el modelo aparece en la UI pero el bot de Telegram no lo ofrece, o al revés.

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

---

## 5. Añadir un modelo nuevo

1. `Client/Pages/Modules.razor` → `AllModels`.
2. `Server/Services/Ai/ModelCatalog.cs` → `AllModels`.
3. `Server/Services/Ai/PricingCatalog.cs` → su tarifa **propia** (no confiar en el
   match por prefijo).
4. Si el proveedor ya anunció su retirada, `ModelLifecycle` con fecha y sustituto.
5. Si acepta imágenes de entrada, `VisionCapability`.

Los tests de `Server.Tests/CatalogoModelos/` fallan si te saltas el paso 3: hay una
comprobación de que **todo** modelo del catálogo tiene tarifa propia, y otra de que
todo modelo retirado indica un sustituto que sigue vivo.

---

## 6. Endpoints y pantalla de precios

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

`GET /api/models/pricing` añade a lo anterior las tarifas de los modelos de texto e
imagen (los de embeddings y audio no entran). Alimenta la pantalla **Precios**
(`/precios`, `Client/Pages/ModelPricing.razor`).

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
