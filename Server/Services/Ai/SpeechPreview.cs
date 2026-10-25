namespace Server.Services.Ai;

/// <summary>
/// Demostracion de voz que se escucha al configurar un modulo de texto a voz
/// (<c>POST /api/voice-preview</c>). La usa OpenAI; ElevenLabs no la necesita
/// porque cada voz de su biblioteca trae una muestra gratis.
/// </summary>
public static class SpeechPreview
{
    /// <summary>Frase de la demostracion. Corta a proposito: cada escucha se factura.</summary>
    public const string Text = "Hola, esta es mi voz. Asi sonara la voz en off de tus videos.";

    /// <summary>
    /// Configuracion de la demostracion con los valores del formulario, aun sin
    /// guardar: se oye lo que se va a guardar, no lo que habia. Las claves son las
    /// mismas que guarda el modulo, asi que la peticion sale igual que al ejecutarlo.
    /// </summary>
    public static Dictionary<string, object> Configuration(string? voice, double? speed, string? instructions)
    {
        var config = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(voice)) config["voice"] = voice.Trim();
        if (speed is double s) config["speed"] = s;
        if (!string.IsNullOrWhiteSpace(instructions)) config[OpenAiSpeech.InstructionsKey] = instructions.Trim();
        return config;
    }
}
