//import { RectNode, RectNodeModel, h } from '/@logicflow/core'
import { getBytesLength } from './common/methods.mjs'

function translateComponentConfig(paramItem) {
  class RedNodeModel extends RectNodeModel {
    /**
     * 初始化
     */
    initNodeData (data) {
      super.initNodeData(data)
      this.width = 100
      this.height = 100
      this.radius = 5
      this.iconPosition = '' // icon位置，left表示左边，'right'表示右边
      this.defaultFill = this.properties.typeColor
    }
    /**
     * 动态设置数据，区别于初始化的数据设定，该部分会响应数据变化(每次properties发生变化会触发)
     */
    setAttributes () {
      if (this.text.value) {
        let width = 30 + getBytesLength(this.text.value) * 9
        width = Math.ceil(width / 20) * 20
        if (width < 100) {
          width = 100
        }
        this.width = width
        this.properties.width = width
      }
      this.text.x = this.x + 10
      this.text.y = this.y - 60

      this.sourceRules = [];
      this.targetRules = [];
      
      this.sourceRules.push({
        message: "只允许从右边的锚点连出",
        validate: (sourceNode, targetNode, sourceAnchor, targetAnchor) => {
            return sourceAnchor.type == "right"
        }
      })
      /**this.sourceRules.push({
        message: "连线两端势流类型不匹配",
        validate: (sourceNode, targetNode, sourceAnchor, targetAnchor) => {
          return sourceNode.outputType === "Any" || targetNode.outputType === "Any" || sourceNode.outputType === targetNode.outputType;
        }
      })*/
      if (paramItem.outCount.max !== -1){
        this.sourceRules.push({
          message: "连线两端势流类型不匹配",
          validate: (sourceNode, targetNode, sourceAnchor, targetAnchor) => {
            return sourceNode.outgoing.edges.length <= paramItem.outCount.max;
          }
        })
      }
      this.targetRules.push({
        message: "只允许连接左边的锚点",
        validate: (sourceNode, targetNode, sourceAnchor, targetAnchor) => {
            return targetAnchor.type == "left"
        }
      })
      if (paramItem.inCount.max !== -1){
        this.targetRules.push({
          message: "连线两端势流类型不匹配",
          validate: (sourceNode, targetNode, sourceAnchor, targetAnchor) => {
            return targetNode.incoming.edges.length <= paramItem.inCount.max;
          }
        })
      }
    }
    updateText (val) {
      super.updateText(val)
      this.setAttributes()
    }
    /**
     * 重写节点样式
     */
    getNodeStyle () {
      const style = super.getNodeStyle()
      const dataStyle = this.properties.style || {}
      if (this.isSelected) {
        style.strokeWidth = Number(dataStyle.borderWidth) || 2
        style.stroke = dataStyle.borderColor || '#ff7f0e'
      } else {
        style.strokeWidth = Number(dataStyle.borderWidth) || 1
        style.stroke = dataStyle.borderColor || '#999'
      }
      style.fill = dataStyle.backgroundColor || this.defaultFill
      return style
    }
    /**
     * 重写定义锚点
     */
    getDefaultAnchor () {
      const { x, y, id, width, height } = this
      const anchors = []
      if (paramItem.inCount.min > 1){
        for (let i = 0; i<paramItem.inCount.min; i++){
          anchors.push(
            {
              x: x - width/2,
              y: y - height*0.3 + height * 0.6 / (paramItem.inCount.min-1) * (i),
              id: `${id}_left_${i+1}`,
              type: "left"
            }
          )
        }
      } else if (paramItem.inCount.min == 1) {
        anchors.push(
          {
            x: x - width/2,
            y: y,
            id: `${id}_left_1`,
            type: "left"
          }
        )
      }
      if (paramItem.outCount.min > 1){
        for (let i = 0; i<paramItem.outCount.min; i++){
          anchors.push(
            {
              x: x + width/2,
              y: y - height*0.3 + height * 0.6 / (paramItem.outCount.min-1) * (i),
              id: `${id}_right_${i+1}`,
              type: "right"
            }
          )
        }
      } else if (paramItem.outCount.min == 1) {
        anchors.push(
          {
            x: x + width/2,
            y: y,
            id: `${id}_right_1`,
            type: "right"
          }
        )
      }
      return anchors
    }
    /**
     *
     */
    getOutlineStyle () {
      const style = super.getOutlineStyle()
      style.stroke = 'transparent'
      style.hover.stroke = 'transparent'
      return style
    }
    /**
     * 导出时处理导出的数据
     */
    getData () {
      const data = super.getData()
      data.properties.ui = 'node-red'
      return data
    }
  }


  class RedNodeView extends RectNode {
    /**
     * 1.1.7版本后支持在view中重写锚点形状。
     * 重写锚点新增
     */
    getAnchorShape (anchorData) {
      const { x, y, type } = anchorData
      return h("rect", {
        type,
        x: x - 5,
        y: y - 5,
        width: 10,
        height: 10,
        className: 'custom-anchor'
      })
    }
    getShape () {
      const {
        text,
        x,
        y,
        width,
        height,
        radius
      } = this.props.model
      const style = this.props.model.getNodeStyle()
      return h(
        'g',
        {
          className: 'lf-red-node'
        },
        [
          h('rect', {
            ...style,
            x: x - width / 2,
            y: y - height / 2,
            width,
            height,
            rx: radius,
            ry: radius
          }),
          h('g', {
            style: 'pointer-events: none',
            transform: `translate(${x}, ${y})`
          }, [
            this.getIcon(),
            
          ])
        ]
      )
    }
    getIcon () {
      const { width, height } = this.props.model
      return h('image', {
        width,
        height,
        x: - width / 2,
        y: - height / 2,
        href: this.props.model.properties.icon
      })
    }
  }


  return {
    type: paramItem.type,
    text: paramItem.text,
    model: RedNodeModel,
    view: RedNodeView
  }
}

export { translateComponentConfig }