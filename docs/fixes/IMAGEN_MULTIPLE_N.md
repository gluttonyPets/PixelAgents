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

> Nota: `InputAdapter.GetMaxPromptLength` asume 4.000 caracteres para toda la
> familia `gpt-image`. OpenAI documenta 32.000 para `gpt-image-1`. Si el límite
> real es mayor, subirlo ahí quita el recorte de raíz.

## Verificación

```bash
dotnet test Server.Tests/Server.Tests.csproj --filter "FullyQualifiedName~ImagenMultiple"
```

Cubre el reparto de las marcas en sus variantes habituales, que un texto sin
marcas no se parta solo, que cada llamada lleve su parte más el contexto común,
que nunca se pida un lote `n>1` en el reparto, y que un puerto sin imagen no
propague nada.

En una ejecución real, el log del paso de imagen debe mostrar
`Reparto multi-imagen: 2 llamada(s) independientes` y una línea `imagen 1/2` e
`imagen 2/2`.
