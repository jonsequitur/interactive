import * as assert from 'assert';

import * as vscode from 'vscode';
// import * as myExtension from '../../extension';

async function withEvent<T>(event: vscode.Event<T>, callback: (e: Promise<T>) => Promise<void>) {
    const e = asPromise<T>(event);
    await callback(e);
}


export async function asPromise<T>(event: vscode.Event<T>, timeout = 30000): Promise<T> {
    const error = new Error('asPromise TIMEOUT reached');
    return new Promise<T>((resolve, reject) => {

        const handle = setTimeout(() => {
            sub.dispose();
            reject(error);
        }, timeout);

        const sub = event(e => {
            clearTimeout(handle);
            sub.dispose();
            resolve(e);
        });
    });
}

suite('Notebook integration tests', () => {

    const notebookFile = vscode.Uri.file(`C:\\dev\\interactive\\src\\polyglot-notebooks-vscode-insiders\\tests\\vscode-common-tests\\integration\\test.ipynb`);

    test('TRIES A THING', async () => {

        const notebook = await vscode.workspace.openNotebookDocument(notebookFile);
        const notebookEditor = await vscode.window.showNotebookDocument(notebook);

        // FIX wait for the kernel to be selected
        await new Promise(resolve => setTimeout(resolve, 10000));

        await vscode.commands.executeCommand('notebook.selectKernel', {
            notebookEditor,
            // Value from `constants.ts` is `const NotebookControllerId = 'polyglot-notebook';`
            id: 'polyglot-notebook',
            extension: 'ms-dotnettools.dotnet-interactive-vscode'
        });

        console.log('Running cell...');

        // await withEvent(vscode.workspace.onDidChangeNotebookDocument, async event => {
        // await vscode.commands.executeCommand<void>('notebook.cell.execute', { start: 1, end: 2 });
        await vscode.commands.executeCommand<void>('notebook.execute');
        // await event;
        // assert.strictEqual(cell.outputs.length, 1, 'should execute'); // runnable, it worked
        // });

        console.log('Ran cell');
        // Now that execution has been completed,
        // you can inspect the contents of the notebook, e,g. outputs using the VS Code API.

        // Inspect outputs
        const cell = notebook.cellAt(1);
        const output = cell.outputs[0];
        console.log(`output: ${JSON.stringify(output)}`);
    });

});
