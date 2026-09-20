using System.Security.Cryptography;
using System.Text;

namespace TicketNow.QueueService;

/// <summary>
/// Proof-of-work didáctico (Fase 6, docs/03 §12): hashcash de ~N bits para
/// entrar a la fila en onsales "bandera". El servidor emite un challenge de
/// un solo uso (Redis, TTL 2 min); el cliente busca un nonce tal que
/// SHA256("challengeId:nonce") tenga `difficulty` bits en cero al inicio.
/// 16 bits ≈ 65k hashes (< 1 s en JS); 20 bits ≈ 1M (≈ segundos).
/// Frena scripts tontos; no es un captcha ni resiste GPUs (documentado).
/// </summary>
public static class ProofOfWork
{
    public static bool Verify(string challengeId, string nonce, int difficulty)
    {
        if (string.IsNullOrWhiteSpace(challengeId) || string.IsNullOrWhiteSpace(nonce) || difficulty < 1)
        {
            return false;
        }
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes($"{challengeId}:{nonce}"));
        var fullBytes = difficulty / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (hash[i] != 0)
            {
                return false;
            }
        }
        var remaining = difficulty % 8;
        if (remaining > 0)
        {
            var mask = 0xFF << (8 - remaining);
            if ((hash[fullBytes] & mask) != 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Solucionador de referencia (tests y ejemplo de cliente). O(n) ingenua.</summary>
    public static string Solve(string challengeId, int difficulty, int maxAttempts = 10_000_000)
    {
        for (var nonce = 0; nonce < maxAttempts; nonce++)
        {
            var candidate = nonce.ToString();
            if (Verify(challengeId, candidate, difficulty))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException($"sin solución en {maxAttempts} intentos (dificultad {difficulty})");
    }
}
