from pathlib import Path

root = Path(__file__).resolve().parents[2]
management_path = root / 'src/SirkAgent.Service/ManagementWorker.cs'
management = management_path.read_text(encoding='utf-8-sig')

old = '''            var current = existing.Keys.Select(ValidateTrustedPolicyKey)
                .OrderBy(value => value.KeyId, StringComparer.Ordinal)
                .ToArray();
            if (current.Length != normalized.Length ||
                current.Where((value, index) =>
                        !string.Equals(value.KeyId, normalized[index].KeyId, StringComparison.Ordinal) ||
                        !PublicKeysEqual(value.PublicKeyPem, normalized[index].PublicKeyPem))
                    .Any())
            {
                throw new InvalidDataException(
                    "Portal attempted to replace an established trusted policy key set.");
            }
            return;
'''
new = '''            var current = existing.Keys.Select(ValidateTrustedPolicyKey)
                .OrderBy(value => value.KeyId, StringComparer.Ordinal)
                .ToArray();
            if (current.Length == 0)
            {
                AtomicFile.WriteJson(path, new TrustedKeyDocument(normalized), _json);
                return;
            }
            if (current.Length != normalized.Length ||
                current.Where((value, index) =>
                        !string.Equals(value.KeyId, normalized[index].KeyId, StringComparison.Ordinal) ||
                        !PublicKeysEqual(value.PublicKeyPem, normalized[index].PublicKeyPem))
                    .Any())
            {
                throw new InvalidDataException(
                    "Portal attempted to replace an established trusted policy key set.");
            }
            return;
'''
if management.count(old) != 1:
    raise RuntimeError(f'empty trust bootstrap block: expected one occurrence, found {management.count(old)}')
management = management.replace(old, new, 1)
management_path.write_text(management, encoding='utf-8', newline='\n')

contract_path = root / 'tests/canonical-agent-management-v1-contract.ps1'
contract = contract_path.read_text(encoding='utf-8-sig')
old_marker = '''    'Portal attempted to replace an established trusted policy key set',
    'PublicKeysEqual',
'''
new_marker = '''    'Portal attempted to replace an established trusted policy key set',
    'if (current.Length == 0)',
    'AtomicFile.WriteJson(path, new TrustedKeyDocument(normalized), _json)',
    'PublicKeysEqual',
'''
if contract.count(old_marker) != 1:
    raise RuntimeError(f'contract marker: expected one occurrence, found {contract.count(old_marker)}')
contract = contract.replace(old_marker, new_marker, 1)
contract_path.write_text(contract, encoding='utf-8', newline='\n')

print('Authenticated policy key bootstrap now accepts an empty existing trust store.')
