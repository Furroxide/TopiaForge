@{
    Severity = @('Error', 'Warning')
    # These rules enforce naming/output style rather than safety or
    # correctness. Repository-facing scripts deliberately use Write-Host for
    # progress and retain stable public function names for compatibility.
    ExcludeRules = @(
        'PSAvoidUsingWriteHost',
        'PSUseApprovedVerbs',
        'PSUseSingularNouns'
    )
}
