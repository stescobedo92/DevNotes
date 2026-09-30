namespace DevNotes.Desktop.Tests.Support;

public static class SampleVault
{
    public const string DeadlockNote = """
        ---
        id: 01J8ZQ4M9T3N7K5W2X6Y8V0B1C
        title: Deadlock en actualización de inventario
        project: azure-microservices
        tags: [sql-server, deadlock, performance]
        type: bug
        created: 2026-09-12
        updated: 2026-09-14
        links:
          commits: [a3f9c21, 7be04d8]
          tickets: [MS-482]
        ---

        # Contexto

        El proceso nocturno de **inventario** se bloqueaba con `UPDATE` concurrentes.

        ## Solución

        1. Reordenar los accesos a las tablas.
        2. Añadir un índice de cobertura.

        ```sql
        CREATE INDEX IX_Inventory_Sku ON dbo.Inventory (Sku) INCLUDE (Quantity);
        ```

        ```csharp
        public async Task UpdateInventoryAsync(IReadOnlyList<Item> items, CancellationToken ct)
        {
            await using var transaction = await _db.BeginTransactionAsync(ct);
            // ...
        }
        ```

        > Ver también [la documentación](https://learn.microsoft.com/sql) y <script>alert('xss')</script>.

        | Antes | Después |
        |------:|:--------|
        | 12 s  | 0.4 s   |
        """;

    public const string RunbookNote = """
        ---
        id: 01J8ZQ4M9T3N7K5W2X6Y8V0B2D
        title: Runbook de despliegue
        project: oatpp-api
        tags: [azure, deploy]
        type: runbook
        updated: 2026-09-20
        ---

        # Pasos

        - [x] Compilar en Release
        - [ ] Publicar el contenedor
        """;

    public const string PlainNote = "# Apuntes sueltos\n\nTexto sin frontmatter.\n";

    /// <summary>Writes the three sample notes into the vault folder.</summary>
    public static void Seed(TempDirectory vault)
    {
        vault.Write("bugs/deadlock-inventario.md", DeadlockNote);
        vault.Write("runbooks/despliegue.md", RunbookNote);
        vault.Write("apuntes.md", PlainNote);
    }
}
