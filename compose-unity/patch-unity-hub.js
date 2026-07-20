const fs = require('fs');

const file = 'C:\\Program Files\\Unity Hub\\resources\\app.asar';
const originalOptions = Buffer.from(
    'override:!1,removeOnFail:!1,removeOnStop:!1,retry:!1,'
);
const resilientOptions = Buffer.from(
    'removeOnFail:!1,removeOnStop:!1,retry:{maxRetries:5},'.padEnd(
        originalOptions.length,
        ' ',
    ),
);
const data = fs.readFileSync(file);
const matches = [];

for (
    let index = data.indexOf(originalOptions);
    index >= 0;
    index = data.indexOf(originalOptions, index + 1)
) {
    matches.push(index);
}

if (matches.length !== 1) {
    throw new Error(`Expected one Hub download option block, found ${matches.length}`);
}

for (const index of matches) {
    resilientOptions.copy(data, index);
}

fs.writeFileSync(file, data);
