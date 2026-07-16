import { existsSync } from 'node:fs';
import { dirname, relative, resolve, sep } from 'node:path';

import { isWithinRoot } from '../lib/link-utils.mjs';

const markdownLinkPattern = /(!?\[[^\]]*\]\()([^)\s]+)(\))/gu;

export class LocalDocumentationLinkRewriter {
  #failures;
  #publishedAssets = new Map();
  #repositoryRoot;
  #sourceToRoute;

  constructor(repositoryRoot, sourceToRoute, failures) {
    this.#repositoryRoot = repositoryRoot;
    this.#sourceToRoute = sourceToRoute;
    this.#failures = failures;
  }

  get publishedAssets() {
    return this.#publishedAssets;
  }

  rewrite(pagePath, content) {
    return content.replace(markdownLinkPattern, (whole, prefix, target, suffix) => {
      if (isPreservedTarget(target)) {
        return whole;
      }

      const [pathPart, fragment] = target.split('#', 2);
      let decodedPath;
      try {
        decodedPath = decodeURIComponent(pathPart);
      } catch (error) {
        this.#failures.push(
          `${pagePath}: invalid URL encoding in local repository link ${target}: ${error.message}`,
        );
        return whole;
      }

      const sourceDirectory = dirname(resolve(this.#repositoryRoot, pagePath));
      const linkedAbsolute = resolve(sourceDirectory, decodedPath);
      if (!isWithinRoot(this.#repositoryRoot, linkedAbsolute)) {
        this.#failures.push(`${pagePath}: local repository link escapes the repository: ${target}`);
        return whole;
      }

      const linkedSource = relative(this.#repositoryRoot, linkedAbsolute).split(sep).join('/');
      if (!pathPart.toLowerCase().endsWith('.md')) {
        if (!existsSync(linkedAbsolute)) {
          this.#failures.push(`${pagePath}: local repository link does not exist: ${target}`);
          return whole;
        }

        this.#publishedAssets.set(linkedSource, linkedAbsolute);
        const publishedPath = linkedSource
          .split('/')
          .map((segment) => encodeURIComponent(segment))
          .join('/');
        return `${prefix}/source/${publishedPath}${fragment ? `#${fragment}` : ''}${suffix}`;
      }

      const route = this.#sourceToRoute.get(linkedSource);
      if (!route) {
        this.#failures.push(
          `${pagePath}: local documentation link is not published by Starlight: ${target}`,
        );
        return whole;
      }

      return `${prefix}${route}${fragment ? `#${fragment}` : ''}${suffix}`;
    });
  }
}

function isPreservedTarget(target) {
  return target.startsWith('#')
    || target.startsWith('/')
    || target.startsWith('//')
    || /^[A-Za-z][A-Za-z0-9+.-]*:/u.test(target)
    || target.includes('{{');
}
