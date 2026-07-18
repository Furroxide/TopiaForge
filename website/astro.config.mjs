// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
  site: 'https://docs.topiaforge.dev',
  redirects: {
    '/diagnostics/TF1001': '/reference/diagnostics/#tf1001',
    '/diagnostics/TF1002': '/reference/diagnostics/#tf1002',
    '/diagnostics/TF1003': '/reference/diagnostics/#tf1003',
    '/diagnostics/TF1004': '/reference/diagnostics/#tf1004',
    '/diagnostics/TF1005': '/reference/diagnostics/#tf1005',
    '/diagnostics/TF1006': '/reference/diagnostics/#tf1006',
    '/diagnostics/TF1007': '/reference/diagnostics/#tf1007',
    '/diagnostics/TF1101': '/reference/diagnostics/#tf1101',
    '/diagnostics/TFDEV100': '/reference/diagnostics/#tfdev100',
    '/diagnostics/TFDEV105': '/reference/diagnostics/#tfdev105',
    '/diagnostics/TFDEV110': '/reference/diagnostics/#tfdev110',
    '/diagnostics/TFDEV120': '/reference/diagnostics/#tfdev120',
    '/diagnostics/TFDEV130': '/reference/diagnostics/#tfdev130',
    '/diagnostics/TFDEV140': '/reference/diagnostics/#tfdev140',
    '/diagnostics/TFDEV150': '/reference/diagnostics/#tfdev150',
    '/diagnostics/TFDEV160': '/reference/diagnostics/#tfdev160',
    '/diagnostics/TFDEV170': '/reference/diagnostics/#tfdev170',
    '/diagnostics/TFSCF170': '/reference/diagnostics/#tfscf170',
    '/diagnostics/TFSCF171': '/reference/diagnostics/#tfscf171',
  },
  integrations: [
    starlight({
      title: 'TopiaForge',
      description: 'Build mods for Robotopia with the safe TopiaForge V1 SDK.',
      customCss: ['./src/styles/custom.css'],
      social: [
        {
          icon: 'github',
          label: 'TopiaForge on GitHub',
          href: 'https://github.com/furroxide/TopiaForge',
        },
      ],
      sidebar: [
        {
          label: 'Start here',
          items: [
            { label: 'Overview', slug: 'index' },
            { label: 'Your first mod', slug: 'getting-started/first-mod' },
            { label: 'SDK overview', slug: 'reference/sdk-overview' },
          ],
        },
        {
          label: 'Build mods',
          items: [
            { label: 'Core services', slug: 'guides/core-services' },
            { label: 'In-game UI', slug: 'guides/ui' },
            { label: 'Specialist modules', slug: 'guides/modules' },
            { label: 'RobotKit', slug: 'guides/robotkit' },
            { label: 'Custom worlds and modes', slug: 'guides/custom-worlds' },
            { label: 'Test a mod', slug: 'guides/testing' },
            { label: 'Development loop', slug: 'guides/cli-dev' },
            { label: 'Advanced interop', slug: 'guides/interop' },
          ],
        },
        {
          label: 'Reference',
          items: [
            { label: 'Manifest V4', slug: 'reference/manifest-v4' },
            { label: 'Diagnostics', slug: 'reference/diagnostics' },
            { label: 'Capabilities and trust', slug: 'reference/capabilities' },
            { label: 'V1 capability coverage', slug: 'reference/capability-matrix' },
            { label: 'Live Robotopia acceptance', slug: 'reference/live-game-acceptance' },
            { label: 'Package format', slug: 'reference/package-format' },
            { label: 'Compatibility policy', slug: 'reference/compatibility' },
            { label: 'Troubleshooting', slug: 'reference/troubleshooting' },
            {
              label: 'C# API reference',
              link: '/api/csharp/',
              attrs: { target: '_self' },
            },
          ],
        },
        {
          label: 'Share',
          items: [{ label: 'Publish a mod', slug: 'guides/publishing' }],
        },
      ],
    }),
  ],
});
