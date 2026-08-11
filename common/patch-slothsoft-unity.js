const fs = require('fs');

const sourcePath = process.argv[2];
if (!sourcePath) {
    throw new Error('Usage: node patch-slothsoft-unity.js path/to/UnityHub.php');
}

let source = fs.readFileSync(sourcePath, 'utf8');

function replaceOnce(search, replacement, description) {
    const first = source.indexOf(search);
    if (first < 0 || source.indexOf(search, first + search.length) >= 0) {
        throw new Error(`Expected exactly one ${description}`);
    }
    source = source.replace(search, replacement);
}

replaceOnce(
    "    private const UNITY_ARCHIVE_ALPHA = 'https://unity.com/releases/editor/alpha/';",
    "    private const UNITY_ARCHIVE_ALPHA = 'https://unity.com/releases/editor/alpha/';\n\n" +
        "    private const UNITY_RELEASE_API = 'https://services.api.unity.com/unity/editor/release/v1/releases';",
    'Unity archive constant'
);

replaceOnce(
    "        if (str_contains($version, 'f')) {\n" +
        "            $this->loadChangesetsFromUrl(self::UNITY_ARCHIVE_FINAL . preg_replace('~f.*~', '', $version));",
    "        if (str_contains($version, 'f')) {\n" +
        "            $this->loadChangesetFromReleaseApi($version);\n\n" +
        "            if (isset($this->changesets[$version])) {\n" +
        "                return $this->changesets[$version];\n" +
        "            }\n\n" +
        "            $this->loadChangesetsFromUrl(self::UNITY_ARCHIVE_FINAL . preg_replace('~f.*~', '', $version));",
    'final-release changeset fallback'
);

replaceOnce(
    "    public function createModuleInstallation(string $version, array $modules = []): array {",
    "    private function loadChangesetFromReleaseApi(string $version): void {\n" +
        "        $url = self::UNITY_RELEASE_API . '?' . http_build_query([\n" +
        "            'limit' => 1,\n" +
        "            'version' => $version\n" +
        "        ]);\n" +
        "        $json = file_get_contents($url, false, $this->getFileContext());\n" +
        "        $response = is_string($json) ? json_decode($json, true) : null;\n" +
        "        $release = is_array($response) ? ($response['results'][0] ?? null) : null;\n" +
        "        $changeset = is_array($release) ? ($release['shortRevision'] ?? null) : null;\n\n" +
        "        if (($release['version'] ?? null) === $version and is_string($changeset) and preg_match('~^[a-f0-9]{12}$~', $changeset)) {\n" +
        "            $this->changesets[$version] = $changeset;\n" +
        "        }\n" +
        "    }\n\n" +
        "    public function createModuleInstallation(string $version, array $modules = []): array {",
    'module installation method'
);

fs.writeFileSync(sourcePath, source);
