namespace InGameCompanion.Ai;

/// <summary>
/// Modele "önceki konuşma" olarak gönderilen tek soru-cevap.
/// (v0.2: Kalıcı, oyun başına hafıza için bkz. Core/GameMemoryStore.cs)
/// </summary>
internal sealed record ChatTurn(string Question, string Answer);
