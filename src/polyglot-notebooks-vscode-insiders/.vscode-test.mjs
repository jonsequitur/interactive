import { defineConfig } from '@vscode/test-cli';

export default defineConfig({
	files: 'out/tests/**/integration/**/*.test.js',
	mocha: {
		timeout: 60000
	}
});
