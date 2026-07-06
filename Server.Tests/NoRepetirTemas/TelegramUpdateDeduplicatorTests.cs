using System;
using Server.Services.Telegram;
using Xunit;

namespace Server.Tests.NoRepetirTemas;

/// <summary>
/// Tests para <see cref="TelegramUpdateDeduplicator"/>, el guard de idempotencia que evita
/// que un mismo update de Telegram (reintento de webhook o reproceso de polling) se maneje
/// dos veces y la publicacion se envie duplicada al chat.
/// </summary>
public class TelegramUpdateDeduplicatorTests
{
    [Fact]
    public void TryMarkProcessed_PrimeraVez_DevuelveTrue()
    {
        var dedup = new TelegramUpdateDeduplicator();

        Assert.True(dedup.TryMarkProcessed("chat1:100"));
    }

    [Fact]
    public void TryMarkProcessed_MismaClaveDosVeces_SegundaDevuelveFalse()
    {
        var dedup = new TelegramUpdateDeduplicator();

        Assert.True(dedup.TryMarkProcessed("chat1:100"));
        Assert.False(dedup.TryMarkProcessed("chat1:100"));
    }

    [Fact]
    public void TryMarkProcessed_ClavesDistintas_TodasDevuelvenTrue()
    {
        var dedup = new TelegramUpdateDeduplicator();

        // Distinto update_id en el mismo chat, o mismo update_id en chats distintos:
        // ninguno es duplicado del otro.
        Assert.True(dedup.TryMarkProcessed("chat1:100"));
        Assert.True(dedup.TryMarkProcessed("chat1:101"));
        Assert.True(dedup.TryMarkProcessed("chat2:100"));
    }

    [Fact]
    public void TryMarkProcessed_DentroDelPeriodo_SigueSiendoDuplicado()
    {
        var dedup = new TelegramUpdateDeduplicator(TimeSpan.FromMinutes(10));
        var t0 = new DateTime(2026, 5, 3, 10, 0, 0, DateTimeKind.Utc);

        Assert.True(dedup.TryMarkProcessed("chat1:100", t0));
        // Un reintento del webhook llega segundos/minutos despues: sigue siendo duplicado.
        Assert.False(dedup.TryMarkProcessed("chat1:100", t0.AddMinutes(5)));
    }

    [Fact]
    public void TryMarkProcessed_TrasCaducar_VuelveAProcesar()
    {
        var dedup = new TelegramUpdateDeduplicator(TimeSpan.FromMinutes(10));
        var t0 = new DateTime(2026, 5, 3, 10, 0, 0, DateTimeKind.Utc);

        Assert.True(dedup.TryMarkProcessed("chat1:100", t0));
        // Pasado el periodo de retencion la entrada caduca; Telegram nunca reintenta un
        // update tan antiguo, asi que la clave puede reutilizarse sin riesgo.
        Assert.True(dedup.TryMarkProcessed("chat1:100", t0.AddMinutes(11)));
    }

    [Fact]
    public void TryMarkProcessed_ClaveVacia_SiempreDejaPasar()
    {
        var dedup = new TelegramUpdateDeduplicator();

        // Sin update_id no hay nada sobre lo que deduplicar: no debe bloquear el update.
        Assert.True(dedup.TryMarkProcessed(""));
        Assert.True(dedup.TryMarkProcessed(""));
    }
}
