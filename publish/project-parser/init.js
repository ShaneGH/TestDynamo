const fs = require("fs")
const path = require("path")
var xml2js = require('xml2js')

execute();

async function execute() {
    const parser = new xml2js.Parser(/* options */);
    var builder = new xml2js.Builder({headless: true});

    let args = [...process.argv].slice(2)
    const vFlag = args.indexOf("--version")
    if (vFlag === -1 || vFlag >= args.length) {
        throw new Error('Input args: --version {version} {file1} {file2} {file3}...');
    }

    const packageVersion = args[vFlag + 1]
    args.splice(vFlag, 2)

    const files = await Promise.all(args
        .map(x => x.trim())
        .reduce((s, x) => s.indexOf(x) === -1 ? [...s, x] : s, [])
        .map(ensureAbsolute)
        .map(x => parser
            .parseStringPromise(fs.readFileSync(x).toString())
                .then(fileXml => ({
                    path: x, 
                    split: x.split(/\\|\//),
                    file: fs.readFileSync(x).toString(),
                    fileXml
                }))))
        .then(xs => xs
            .map(x => makeProjectReferencesAbsolute(x, packageVersion))
            .map(addPackageName)
            .map(x => addVersion(x, packageVersion)));

    if (!files.length) {
        return
    }

    processFiles(files)
    files.forEach(file => {
        console.log(`Writing file ${file.path}`)
        fs.writeFileSync(file.path, builder.buildObject(file.fileXml))
    })
}

function addPackageName(file) {
    file.packageName = file.fileXml.Project.PropertyGroup[0].PackageId?.[0]
    // tool also fixes test projects. These will not have packageNames
    // if (!file.packageName) throw new Error(`Could not find package name for ${file.path}`);
    return file
}

function addVersion(file, packageVersion) {
    const props = file.fileXml.Project.PropertyGroup[0]
    if (props) props.Version = packageVersion
    // tool also fixes test projects. These will not have packageNames
    // if (!file.packageName) throw new Error(`Could not find package name for ${file.path}`);
    return file
}

function ensureAbsolute(path) {
    if (/^[a-zA-Z]+:(\\|\/)/.test(path)) return path
    throw new Error(`Path ${path} must be absolute`);
}

function makeProjectReferencesAbsolute(file, packageVersion) {
    const dependencies = file.fileXml.Project.ItemGroup.reduce((dependencies, itemGroup) => {
        const prs = itemGroup.ProjectReference instanceof Array 
            ? itemGroup.ProjectReference
            : itemGroup.ProjectReference 
                ? [itemGroup.ProjectReference]
                : []

        for (let i = 0; i < prs.length; i++) {
            const pr = prs[i]
            if (!pr.$ || !pr.$.Include) {
                console.error(pr)
                throw new Error(`Could not understand project reference for ${file.path}`);
            }

            dependencies.push({ 
                name: path.resolve(path.parse(file.path).dir, pr.$.Include), 
                setter: replacer.bind(null, pr.$.Include) 
            })
        }

        return dependencies

        function replacer(reference, packageName) {
            console.log(`Replacing project ref ${reference} to package ref ${packageName} for project ${file.path}`)

            remover(reference)

            const ig = newItemGroupFinder()
            ig.PackageReference.push({
                $: {
                    Include: packageName,
                    Version: packageVersion
                }
            })
        }

        function newItemGroupFinder() {
            for (let i = 0; i < file.fileXml.Project.ItemGroup.length; i++) {
                const ig = file.fileXml.Project.ItemGroup[i]
                if (ig.$ && ig.$.Label === "DependencyReplace") {
                    return ig
                }
            }
            
            const newItemGroup = {
                PackageReference: [],
                $: {
                    Label: "DependencyReplace"
                }
            }

            file.fileXml.Project.ItemGroup.push(newItemGroup)
            return newItemGroup
        }

        function remover(reference) {
            if (!itemGroup.ProjectReference) throw new Error(`Cannot find reference ${reference}`);

            const prs = itemGroup.ProjectReference
            if (prs instanceof Array) {
                for (let i = 0; i < prs.length; i++) {
                    const pr = prs[i]
                    if (pr.$ && pr.$.Include === reference) {
                        prs.splice(i, 1)
                        if (!prs.length) {
                            delete itemGroup.ProjectReference
                        }

                        if (!Object.keys(itemGroup).length) {
                            const i = file.fileXml.Project.ItemGroup.indexOf(itemGroup)
                            if (i >= 0) file.fileXml.Project.ItemGroup.splice(i, 1)
                        }

                        return
                    }
                }
                
                throw new Error(`Cannot find reference ${reference}`);
            }
            
            if (prs.$ && prs.$.Include === reference) {
                delete itemGroup.ProjectReference

                if (!Object.keys(itemGroup).length) {
                    const i = file.fileXml.Project.ItemGroup.indexOf(itemGroup)
                    if (i >= 0) file.fileXml.Project.ItemGroup.splice(i, 1)
                }

                return
            }
            
            throw new Error(`Cannot find reference ${reference}`);
        }
    }, [])

    return { ...file, dependencies }
}

function processFiles(files) {
    if (!files.length) return

    const head = files.filter(x => !x.dependencies.length)[0]
    if (!head) throw new Error("Not all dependencies are contained in tree")
    const tail = files.filter(x => x !== head)

    if (head.packageName) {
        tail.forEach(dependant => {
            for (let i = dependant.dependencies.length - 1; i >= 0; i--) {
                const dep = dependant.dependencies[i]
                if (dep.name === head.path) {
                    dep.setter(head.packageName)
                    dependant.dependencies.splice(i, 1)
                }
            }
        })
    } else {
        console.warn(`Skipping project ${head.path} with no package name`);
    }

    processFiles(tail)
}