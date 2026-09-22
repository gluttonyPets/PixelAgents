# Fix: un módulo de imagen con varias salidas generaba la misma imagen repetida

> Estado: **resuelto** · Área: `Server/Services/Ai/Handlers/ImageModuleHandler.cs`,
> `Server/Services/Ai/Handlers/TextModuleHandler.cs`, `Server/Services/Ai/MultiImagePrompt.cs`,
> `Server/Services/Ai/PortDataResolver.cs`

## Problema

Un nodo de imagen configurado con 2 salidas (`n=2`) no generaba dos imágenes
distintas: devolvía dos variantes de la **misma** composición, cada una con
todas las secciones del diseño dentro (el "antes" y el "después" en la misma
imagen, dos veces). A veces solo llegaba una imagen y se enviaba duplicada por
los dos puertos.

## Causa

Tres fallos encadenados:

1. **`n` no significa "n partes".** Para la API de imágenes, `n` son muestras
   independientes del mismo prompt: todas reciben el texto entero y no hay
   estado compartido entre ellas. El handler hacía **una sola llamada** con
   `n=2`, así que era imposible obtener contenidos distintos.
2. **La regla de desagregación nunca se enviaba.** El texto "Desagregacion
   multi-imagen (n=2)" que aparecía en el panel de reglas y en el JSON exportado
   vivía solo en el cliente (`ActiveRulesRegistry`, usado por el inspector y el
   export). El prompt real de imagen se compone en `OpenAiProvider` y no la
   incluía. Y aunque la incluyera, el punto 1 la dejaba sin efecto.
3. **El planificador no sabía que había dos imágenes.** La config
   `isImagePrompt`/`imageCount` del módulo de texto se escribía en la UI y no la
   leía nadie en el servidor, así que el modelo escribía un único prompt
   compuesto.

Además, `PortDataResolver` hacía que un puerto sin imagen correspondiente
propagara **todas** las imágenes, de ahí la misma imagen enviada dos veces.

## Solución

Contrato único en `MultiImagePrompt`, con las dos puntas conectadas:

- `TextModuleHandler` detecta si a su salida hay un módulo de imagen con más de
  una salida (o lee `imageCount` de su propia config) y antepone al prompt la
  instrucción de escribir un bloque por imagen separado por `===IMAGEN 1===`,
  `===IMAGEN 2===`, ...
- `ImageModuleHandler` reparte el texto por esas marcas y hace **una llamada por
  parte con `n=1`**. Lo anterior a la primera marca —y lo que llega por el mismo
  puerto sin marcas, como el índice del módulo Directorio— es contexto común y
  se antepone a todas. Cada llamada resuelve sus propias imágenes de referencia.
- `PortDataResolver`: el puerto `output_image_i` entrega la imagen *i* o no
  entrega nada.
- La regla del cliente pasa a describir el mecanismo real, para que el export no
  anuncie una instrucción que nunca se manda.

Sin marcas en el texto no hay reparto posible: se mantiene la llamada única y
queda avisado en el log de la ejecución, igual que cuando el número de partes no
coincide con el de salidas.

## Segunda vuelta: el recorte se comía la parte propia

Con el reparto ya funcionando, las imágenes seguían saliendo iguales. El log lo
enseñaba:

```
imagen 1/2 ... prompt 5775 chars
[AVISO] El prompt fue recortado de 6,452 a 4,000 caracteres
```

El contexto común (índice del Directorio + concepto del diseñador) iba delante y
la parte propia de cada imagen al final. El proveedor trunca por el final contra
el límite del modelo, así que el recorte se llevaba los ~2.450 caracteres de la
escena y las dos llamadas acababan enviando prácticamente el mismo texto.

Ahora la parte propia va **primero** y el módulo reparte el presupuesto antes de
llamar: descuenta lo que el proveedor antepone (regla de idioma, contexto del
proyecto, `systemPrompt`) y recorta el contexto común, no la escena. Además, una
vez descargadas las referencias, las URLs del directorio se sustituyen por el
nombre del fichero (~130 caracteres menos por cita, y una URL menos que el modelo
pueda dibujar como texto). Como efecto secundario, las URLs que cita la escena
entran antes que las del índice en el reparto de referencias.

## Tercera vuelta: el límite era el de otro modelo

El recorte que disparó todo esto no debería haber existido:
`InputAdapter.GetMaxPromptLength` devolvía 4.000 caracteres para toda la familia
`gpt-image`, que es el límite de DALL-E 3. OpenAI documenta **32.000** para
`gpt-image`. Se partían por la mitad prompts perfectamente válidos.

El límite pasa a ser un dato del catálogo (`ModelCatalog.CatalogModel.PromptChars`),
que es de donde salen ya las capacidades y la ventana de contexto, y se ve como
columna "Prompt máx." en la sección de imagen de la pantalla de modelos. Detalles en
[`../CATALOGO_MODELOS.md`](../CATALOGO_MODELOS.md).

Con eso, `InputAdapter` deja de tener números propios (solo un fallback por familia
para ids que no estén en el catálogo) y la lista de modelos que sugiere el aviso de
recorte se calcula del catálogo en vez de una tabla paralela que se quedaba vieja
—y descarta los modelos ya retirados, que antes se sugerían igual—.

## Cuarta vuelta: el contrato JSON de la conexión anulaba el reparto

Un pipeline de vídeo (texto -> imagen x3 -> tres nodos de vídeo) seguía dando
tres imágenes iguales. El log lo dejaba por escrito dos veces:

```
[GPT - Guión para video] El modulo de imagen siguiente pide 3 imagenes, pero esta
  conexion declara un contrato JSON propio y las marcas de reparto lo romperian.
[GPT Imagen estandar] El modulo pide 3 imagenes pero el texto de entrada no viene
  separado en partes (===IMAGEN 1===, ...): se piden las 3 con el MISMO prompt.
```

Eran dos mecanismos que no se hablaban. La conexión llevaba un `Format`
(`{"tomas":[{"prompt":"toma 1"}, ...]}`), porque el mismo módulo de texto
alimentaba también a un orquestador, así que `TextModuleHandler` renunciaba a
planificar y solo avisaba; el módulo de imagen, que únicamente sabía partir por
marcas, recibía el JSON entero y lo mandaba tal cual tres veces. Y como el aviso
pedía "quita el formato de la conexión", tampoco había salida: los formatos se
leen de **todas** las aristas salientes, así que el contrato del orquestador
seguiría desactivando el reparto.

Ahora los dos formatos son válidos:

- `MultiImagePrompt.Split` prueba el JSON cuando no hay marcas: coge la primera
  lista con dos o más elementos, saca de cada elemento su texto (`prompt`,
  `texto`, `content`, ... o los campos simples del objeto) y trata las claves
  sueltas del objeto raíz como contexto común. Si el texto no es JSON, o algún
  elemento sale vacío, no se reparte nada y pasa entero, como antes.
- `TextModuleHandler` ya no se rinde ante un contrato: inyecta
  `BuildJsonPlannerInstruction`, que pide exactamente N elementos autocontenidos
  dentro de la lista del contrato en vez de las marcas.
- Un módulo configurado para 1 imagen que recibe texto con varias partes genera
  una sola imagen con todas dentro, que es lo que su aviso ya prometía (antes
  devolvía las partes y acababa haciendo una llamada por parte).

## Verificación

```bash
dotnet test Server.Tests/Server.Tests.csproj --filter "FullyQualifiedName~ImagenMultiple"
```

Cubre el reparto de las marcas en sus variantes habituales, el reparto por la
lista de un contrato JSON, que un texto sin marcas no se parta solo, que cada llamada lleve su parte más el contexto común,
que nunca se pida un lote `n>1` en el reparto, y que un puerto sin imagen no
propague nada.

En una ejecución real, el log del paso de imagen debe mostrar
`Reparto multi-imagen: 2 llamada(s) independientes` y una línea `imagen 1/2` e
`imagen 2/2`.
