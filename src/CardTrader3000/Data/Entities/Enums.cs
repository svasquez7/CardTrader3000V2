namespace CardTrader3000.Data.Entities;

/// <summary>Price tier from the Phase 1 rules. Derived in code from the list price, never trusted from the model.</summary>
public enum PriceBucket
{
    /// <summary>"Bucket 1 ($2.99+)"</summary>
    Bucket1 = 1,

    /// <summary>"Bucket 2 (Rest)"</summary>
    Bucket2 = 2
}

public enum SgcCandidate
{
    No = 0,
    Secondary = 1,
    HighProspect = 2
}

public enum InventoryStatus
{
    InStock = 0,
    Listed = 1,
    Sold = 2
}

public enum ImportSource
{
    Csv = 0,
    Manual = 1,

    /// <summary>Existing inventory cards sent back through Claude from the Inventory page.</summary>
    Reprice = 2
}

public enum ImportBatchStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    CompletedWithErrors = 3,
    Failed = 4
}

public enum ImportItemStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2
}
