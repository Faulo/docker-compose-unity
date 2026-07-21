const fs = require('fs');

const file = process.argv[2] || 'C:\\Program Files\\Unity Hub\\resources\\app.asar';
const data = fs.readFileSync(file);

function findAll(pattern) {
    const matches = [];
    for (
        let index = data.indexOf(pattern);
        index >= 0;
        index = data.indexOf(pattern, index + 1)
    ) {
        matches.push(index);
    }
    return matches;
}

function replaceOnce(originalText, replacementText, label) {
    const original = Buffer.from(originalText);
    const replacement = Buffer.from(replacementText.padEnd(original.length, ' '));
    if (replacement.length > original.length) {
        throw new Error(`${label} replacement is too large`);
    }

    const originalMatches = findAll(original);
    const replacementMatches = findAll(replacement);
    if (originalMatches.length === 1 && replacementMatches.length === 0) {
        replacement.copy(data, originalMatches[0]);
        return;
    }
    if (originalMatches.length === 0 && replacementMatches.length === 1) {
        return;
    }
    throw new Error(
        `Expected one ${label} block, found ${originalMatches.length} original and ${replacementMatches.length} patched`,
    );
}

replaceOnce(
    'override:!1,removeOnFail:!1,removeOnStop:!1,retry:!1,',
    'removeOnFail:!1,removeOnStop:!1,retry:{maxRetries:5},',
    'Hub download options',
);
replaceOnce(
    'function installFromExe(e,r,t){return logger.debug("installFromExe"),new Promise(((o,n)=>{let i="/S";r&&""!==r&&(i=r);let a="";t&&(a=`/D=${t}`),logger.info(`install ${e} ${i} ${a}`),proc.exec(`"${e}" ${i} ${a}`,{name:"Unity installer"},((e,r)=>{e?n(e):r?n(r):o()}))}))}',
    'function installFromExe(e,r,t){return new Promise(((o,n)=>{let i=r||"/S",a=t?`/D=${t}`:"",s=30,c=()=>proc.exec(`"${e}" ${i} ${a}`,{name:"Unity installer"},((e,r)=>{e&&/another process/.test(e.message)&&s--?setTimeout(c,1000):e?n(e):r?n(r):o()}));setTimeout(c,1000)}))}',
    'Windows editor installer',
);

fs.writeFileSync(file, data);
