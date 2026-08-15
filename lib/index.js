/**
 * dsh-splash-launcher — host half.
 * Ships the Windows DSH-GUI.exe startup-animation launcher inside the npm
 * package and registers a desktop_launch agent tool so the GUI can be opened
 * from a conversation with one tool call.
 */
import { defineTool } from '@deepseek-ai/dsh-tools'
import { spawn } from 'node:child_process'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const exe = join(here, '..', 'DSH-GUI.exe')

export const name = 'splash-launcher'
export const inject = ['tools']

export function apply(ctx) {
  ctx.effect(() => {
    const dispose = ctx.tools.register(defineTool({
      name: 'desktop_launch',
      description:
        'Launch the DSH Web GUI in a desktop window with the splash startup animation (Windows only). ' +
        'Use when the user asks to open/start the DSH GUI, web interface, or desktop window.',
      parameters: {
        workspace: {
          type: 'string',
          description:
            'Optional workspace directory for the launched dsh web session. ' +
            'Defaults to the launcher config (DSH_GUI_WORKSPACE / workspace.txt / D:\\VSCode).',
        },
      },
      output: {
        schema: {
          type: 'object',
          additionalProperties: false,
          properties: {
            ok: { type: 'boolean', required: true },
            pid: { type: 'integer' },
            hint: { type: 'string' },
          },
        },
        render(_args, value) {
          return [{ type: 'text', text: value.hint ?? (value.ok ? 'DSH GUI launched.' : 'launch failed.') }]
        },
      },
      async execute(args) {
        if (process.platform !== 'win32') {
          return { ok: false, hint: 'desktop_launch is Windows-only; this platform is not supported.' }
        }
        try {
          const env = { ...process.env }
          if (args && args.workspace) env.DSH_GUI_WORKSPACE = args.workspace
          const child = spawn(exe, [], { detached: true, stdio: 'ignore', cwd: dirname(exe), env })
          child.unref()
          return { ok: true, pid: child.pid, hint: 'DSH GUI launcher started; the splash shows immediately and the GUI window appears when ready.' }
        } catch (err) {
          return { ok: false, hint: String(err && err.message ? err.message : err) }
        }
      },
    }))
    return dispose
  }, 'dsh-splash-launcher: desktop_launch tool')
}
